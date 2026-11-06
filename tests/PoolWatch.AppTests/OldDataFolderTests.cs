using System.Text.RegularExpressions;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// A pre-release PoolWatch kept its data in the account's own folder, %LOCALAPPDATA%\PoolWatch. PoolWatch, its setup
/// and its uninstaller never read, move or delete that folder, and nothing reads the files the published beta's move
/// of it left in the data folder (".migrated", ".migration-attempts", "*.migrating"; the note "README - PoolWatch data
/// moved.txt" in the old folder). These tests hold that by what the code names, and by what setup's scan asks for:
/// <list type="bullet">
/// <item>the code of every C# file under src/ (its comments left out, its strings kept: <see cref="ScriptCode.CSharpComments"/>)
/// names the account's local AppData folder (the .NET folder or WinForms' path below it, the environment variable or
/// CSIDL, the registry's setting, the folder's own path or "Local Settings", its known-folder id, or .NET's per-user
/// settings by the names <see cref="LocalAppData"/> lists, which are kept below it) only on the lines listed in <see
/// cref="Allowed"/>, each for something else, and each line there is found exactly once;</item>
/// <item>the names those lines give the folder and its setting (<see cref="Aliases"/>: a variable and a constant for the
/// folder, a constant for the setting that holds it) are named only on listed lines too, so no other line builds a path
/// from them;</item>
/// <item>setup's scan of the PC, run over a fake Windows that has none of what it looks for and no version of a tool it
/// tracks, so that it asks for every path it knows (each is held to be asked for), asks for no path in or below the old
/// folder;</item>
/// <item>every file in the installer's folder and below it but its images (the setup script, the installer's scripts)
/// names the folder nowhere, not even in a comment;</item>
/// <item>neither names the files the beta's move left.</item>
/// </list>
/// So a new line of code that names the folder, or names one of those aliases, fails the first two tests until it is
/// added to the list with what it is for. What a scan of names cannot see: a path to the folder built from another
/// folder near it (TEMP's parent, say), the folder's id written as a number, or a path read at run time (from a
/// setting or the registry). Of the code that builds paths in the account's folders, setup's scan is held by what it
/// asks for, whatever the code names.
/// </summary>
public sealed class OldDataFolderTests
{
    /// <summary>
    /// What names the account's local AppData folder: .NET's LocalApplicationData, WinForms' LocalUserAppDataPath, the
    /// LOCALAPPDATA variable and CSIDL_LOCAL_APPDATA, Inno Setup's {localappdata} and {%LOCALAPPDATA}, the registry's
    /// "Local AppData", a path through "AppData\Local" (or "AppData", "Local" as parts) or through the profile's "Local
    /// Settings" (a link to it), the known folder's id, and .NET's per-user settings by these names: ApplicationSettingsBase
    /// and its LocalFileSettingsProvider, which keep user.config below the folder, in one named after the company, and
    /// ConfigurationManager's PerUserRoamingAndLocal, which opens that file.
    /// </summary>
    private static readonly Regex LocalAppData = new(@"local[\s_]*(user[\s_]*)?app(lication)?[\s_]*data|appdata[\\/""\s,]+local|local[\s_]*settings|" +
                                                     @"F1B32785-6FBA-4FCF-9D55-7B8E7F157091|ApplicationSettingsBase|LocalFileSettingsProvider|PerUserRoamingAndLocal",
        RegexOptions.IgnoreCase);

    /// <summary>The names of the files the published beta's move left: its marker, its count of runs, its temporary copies and its note.</summary>
    private static readonly Regex MoveFiles = new(@"\.migrat(ed|ing|ion-attempts)\b|PoolWatch data moved", RegexOptions.IgnoreCase);

    /// <summary>
    /// The lines of C# code under src/ that may name the account's local AppData folder or one of its <see cref="Aliases"/>,
    /// as written (without their indentation), each with what it is for. None of them reaches PoolWatch's old folder.
    /// </summary>
    private static readonly (string File, string Line, string Why)[] Allowed =
    [
        ("src/PoolWatch/Engine/AppPaths.cs", ": Environment.SpecialFolder.LocalApplicationData),",
            "the data folder of the Linux build, which never runs elevated (on Windows it is %ProgramData%\\PoolWatch)"),
        ("src/PoolWatch/Engine/Setup/EnvironmentProbe.cs", "string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);",
            "setup looks for AI tools installed there (VS Code and others), reading only whether their files exist"),
        ("src/PoolWatch/Engine/Setup/EnvironmentProbe.cs",
            "Add(\"VS Code\", Path.Combine(local, \"Programs\", \"Microsoft VS Code\", \"Code.exe\"), Path.Combine(programs, \"Microsoft VS Code\", \"Code.exe\"));",
            "VS Code's per-user install, in the folder's Programs"),
        ("src/PoolWatch/Engine/Setup/EnvironmentProbe.cs", "Add(\"Cursor\", Path.Combine(local, \"Programs\", \"cursor\", \"Cursor.exe\"));",
            "Cursor's per-user install, in the folder's Programs"),
        ("src/PoolWatch/Engine/Setup/EnvironmentProbe.cs",
            "private const string PackagesKey = @\"Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\";",
            "the registry key of the Store packages (HKCU\\Software\\Classes\\Local Settings), not a folder"),
        ("src/PoolWatch/Collectors/VersionCollector.cs",
            "private const string PackagesKey = @\"Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\";",
            "the registry key of the Store packages (HKCU\\Software\\Classes\\Local Settings), not a folder"),
        ("src/PoolWatch.Launcher/EnvironmentBlock.cs",
            "string ProgramData, string Public, string UserProfile, string RoamingAppData, string LocalAppData, string UserName, string UserDomain,",
            "the folders the launcher reads from Windows for the environment it builds"),
        ("src/PoolWatch.Launcher/EnvironmentBlock.cs", "if (IsFullPath(sys.LocalAppData))", "the launcher passes the account's folder on in that environment"),
        ("src/PoolWatch.Launcher/EnvironmentBlock.cs", "Set(\"LOCALAPPDATA\", sys.LocalAppData);", "the launcher passes the account's folder on in that environment"),
        ("src/PoolWatch.Launcher/EnvironmentBlock.cs", "Set(\"TEMP\", Path.Combine(sys.LocalAppData, \"Temp\"));", "the account's TEMP folder, as Windows sets it"),
        ("src/PoolWatch.Launcher/EnvironmentBlock.cs", "Set(\"TMP\", Path.Combine(sys.LocalAppData, \"Temp\"));", "the account's TEMP folder, as Windows sets it"),
        ("src/PoolWatch.Launcher/WindowsPaths.cs", "internal const string RoamingSetting = \"AppData\", LocalSetting = \"Local AppData\";",
            "the name of the account's own setting for the folder, which the launcher reads"),
        ("src/PoolWatch.Launcher/WindowsPaths.cs",
            "internal const string RoamingStandard = @\"%USERPROFILE%\\AppData\\Roaming\", LocalStandard = @\"%USERPROFILE%\\AppData\\Local\";",
            "where Windows puts the folder when that setting is not there"),
        ("src/PoolWatch.Launcher/WindowsPaths.cs", "LocalAppData: UserFolder(Raw(shell, LocalSetting), LocalStandard, profile, drive, windows),",
            "the launcher reads the folder from that setting"),
    ];

    /// <summary>
    /// The names listed lines give the account's local AppData folder or its setting, and the file they can be named in
    /// (null: anywhere under src/). A line of code that names one (as a whole word) must be listed in <see cref="Allowed"/>.
    /// </summary>
    private static readonly (string? File, string Name)[] Aliases =
    [
        ("src/PoolWatch/Engine/Setup/EnvironmentProbe.cs", "local"), // the folder, in AiTools
        (null, "LocalStandard"),                                     // WindowsPaths' constant: the folder's path
        (null, "LocalSetting"),                                      // WindowsPaths' constant: the setting that holds it
    ];

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>Every C# file under src/, but the build's output (bin/, obj/).</summary>
    private static List<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Relative(root, f).Split('/').Any(part => part is "bin" or "obj")).ToList();

    /// <summary>The images of the installer's folder: setup's pages and icons, which hold no text.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".ico", ".bmp" };

    /// <summary>Every file in the installer's folder <paramref name="dir"/> and the folders below it but its images: the setup script and the installer's scripts.</summary>
    private static List<string> InstallerFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !ImageExtensions.Contains(Path.GetExtension(f))).ToList();

    private static List<string> InstallerFiles() => InstallerFiles(Path.Combine(TrayIconsTests.RepoRoot(), "installer"));

    /// <summary>The source with each comment's characters made spaces (its line ends kept), so the lines stay where they are.</summary>
    private static string[] CodeLines(string source)
    {
        var chars = source.ToCharArray();
        foreach (var (start, end) in ScriptCode.CSharpComments(source))
            for (int i = start; i < end; i++)
                if (chars[i] is not ('\r' or '\n')) chars[i] = ' ';
        return new string(chars).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    /// <summary>
    /// Each line of C# code under src/ (in <paramref name="onlyIn"/> alone, when given) that <paramref name="pattern"/>
    /// finds: its file, its number (from 1) and its text, trimmed.
    /// </summary>
    private static List<(string File, int Number, string Text)> CodeMatches(Regex pattern, string? onlyIn = null)
    {
        string root = TrayIconsTests.RepoRoot();
        var found = new List<(string, int, string)>();
        foreach (string file in SourceFiles(root))
        {
            if (onlyIn is not null && Relative(root, file) != onlyIn) continue;
            var lines = CodeLines(File.ReadAllText(file));
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i])) found.Add((Relative(root, file), i + 1, lines[i].Trim()));
        }
        return found;
    }

    /// <summary>The lines of code that name an alias, as a whole word.</summary>
    private static List<(string File, int Number, string Text)> AliasMatches((string? File, string Name) alias) =>
        CodeMatches(new Regex($@"\b{Regex.Escape(alias.Name)}\b"), alias.File);

    private static List<(string File, int Number, string Text)> Unlisted(IEnumerable<(string File, int Number, string Text)> found) =>
        found.Where(f => !Allowed.Any(a => a.File == f.File && a.Line == f.Text)).ToList();

    private static string Lines(IEnumerable<(string File, int Number, string Text)> found) =>
        string.Join("\n", found.Select(u => $"{u.File}:{u.Number}: {u.Text}"));

    [Fact]
    public void TheCodeNamesTheLocalAppDataFolderOnlyOnTheListedLines()
    {
        var found = CodeMatches(LocalAppData);
        var unlisted = Unlisted(found);
        Assert.True(unlisted.Count == 0, "PoolWatch never reads, moves or deletes its old per-user folder, %LOCALAPPDATA%\\PoolWatch. " +
            "These lines name the local AppData folder and are not in OldDataFolderTests.Allowed (add one there, with what it is for, only " +
            "when it is for something else):\n" + Lines(unlisted));
        // Each listed line is there exactly once, so the list says what the code holds (and the scan read the files). A
        // listed line may name an alias rather than the folder itself.
        var all = found.Concat(Aliases.SelectMany(AliasMatches)).Distinct().ToList();
        foreach (var (file, line, _) in Allowed)
            Assert.True(all.Count(f => f.File == file && f.Text == line) == 1, $"{file} does not hold exactly one line \"{line}\"");
    }

    [Fact]
    public void TheNamesTheListedLinesGiveTheFolderAreNamedOnlyOnListedLines()
    {
        foreach (var alias in Aliases)
        {
            var found = AliasMatches(alias);
            Assert.NotEmpty(found); // the name is still there, so this reads it
            var unlisted = Unlisted(found);
            Assert.True(unlisted.Count == 0, $"{alias.Name} holds the account's local AppData folder (or its setting). These lines name it and " +
                "are not in OldDataFolderTests.Allowed (add one there, with what it is for, only when it is for something else):\n" + Lines(unlisted));
        }
    }

    [Fact]
    public async Task SetupsScanAsksForNoPathInTheOldFolder()
    {
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoolWatch");
        string root = Path.Combine(Path.GetTempPath(), "pw-oldfolder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "data"), null, secure: false);
            var log = new Log(Path.Combine(root, "data", "probe.log"));
            var asked = new List<string>();
            // With the ChatGPT desktop app (its icon is looked for) and without it (the Codex CLI's folder is): nothing the
            // scan looks for is there, and no tool's version is known (a version found would skip that tool's path), so it
            // asks for every path it knows.
            foreach (var packages in new List<string>[] { new EnvironmentProbeTests.FakeReads().Packages, [] })
            {
                var reads = new EnvironmentProbeTests.FakeReads { Packages = packages, Versions_ = new(), Exists = p => { lock (asked) asked.Add(p); return false; } };
                await new EnvironmentProbe(paths, log, reads).ScanAsync();
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains(asked, p => p.EndsWith(@"\app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase));
            foreach (string path in new[]
                     {
                         Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe"),
                         Path.Combine(local, "Programs", "cursor", "Cursor.exe"), Path.Combine(profile, ".codex"), Path.Combine(profile, ".claude"),
                     })
                Assert.Contains(path, asked);
            var inOld = asked.Select(Path.GetFullPath)
                .Where(p => p.TrimEnd('\\').Equals(old, StringComparison.OrdinalIgnoreCase) || p.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(inOld.Count == 0, "setup's scan never looks in PoolWatch's old per-user folder:\n" + string.Join("\n", inOld));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SetupAndTheUninstallerNeverNameTheLocalAppDataFolder()
    {
        string root = TrayIconsTests.RepoRoot();
        var files = InstallerFiles();
        Assert.Contains(files, f => Path.GetFileName(f) == "PoolWatch.iss");
        foreach (string file in files)
        {
            var lines = File.ReadAllLines(file);
            var named = Enumerable.Range(0, lines.Length).Where(i => LocalAppData.IsMatch(lines[i])).Select(i => $"{Relative(root, file)}:{i + 1}: {lines[i].Trim()}").ToList();
            Assert.True(named.Count == 0, "setup and the uninstaller never read, move or delete PoolWatch's old per-user folder:\n" + string.Join("\n", named));
        }
    }

    [Fact]
    public void TheInstallersFilesAreReadInEveryFolderBelowIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-installer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "ready", "more"));
            foreach (string f in new[] { "PoolWatch.iss", @"ready\a.png", @"ready\c.isi", @"ready\more\b.ps1", @"ready\more\d.ICO" })
                File.WriteAllText(Path.Combine(dir, f), "");
            Assert.Equal([Path.Combine(dir, "PoolWatch.iss"), Path.Combine(dir, @"ready\c.isi"), Path.Combine(dir, @"ready\more\b.ps1")],
                InstallerFiles(dir).Order(StringComparer.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void NothingNamesTheFilesTheBetasMoveLeft()
    {
        string root = TrayIconsTests.RepoRoot();
        var named = CodeMatches(MoveFiles).Select(f => $"{f.File}:{f.Number}: {f.Text}").ToList();
        foreach (string file in InstallerFiles())
        {
            var lines = File.ReadAllLines(file);
            named.AddRange(Enumerable.Range(0, lines.Length).Where(i => MoveFiles.IsMatch(lines[i])).Select(i => $"{Relative(root, file)}:{i + 1}: {lines[i].Trim()}"));
        }
        Assert.True(named.Count == 0, "nothing reads the marker, the count of runs, the temporary copies or the note the beta's move left:\n" + string.Join("\n", named));
    }

    [Fact]
    public void TheScanFindsTheNamesOfTheFilesTheBetasMoveLeft()
    {
        // As code would name them.
        foreach (string name in new[] { "\".migrated\"", "\".migration-attempts\"", "dest + \".migrating\"", "\"README - PoolWatch data moved.txt\"" })
            Assert.Matches(MoveFiles, name);
    }

    [Fact]
    public void TheScanFindsEachWayOfNamingTheLocalAppDataFolder()
    {
        foreach (string text in new[] { "Environment.SpecialFolder.LocalApplicationData", "Environment.GetEnvironmentVariable(\"LOCALAPPDATA\")",
                     "System.Windows.Forms.Application.LocalUserAppDataPath", "const int CSIDL_LOCAL_APPDATA = 0x1c;", "FOLDERID_LocalAppData",
                     "{localappdata}\\PoolWatch", "{%LOCALAPPDATA}", "@\"C:\\Users\\x\\AppData\\Local\\PoolWatch\"", "\"C:\\\\Users\\\\x\\\\AppData\\\\Local\"",
                     "Path.Combine(profile, \"AppData\", \"Local\", \"PoolWatch\")", "Path.Combine(profile, \"Local Settings\", \"PoolWatch\")",
                     "new Guid(\"F1B32785-6FBA-4FCF-9D55-7B8E7F157091\")", "sealed class OldSettings : ApplicationSettingsBase",
                     "[SettingsProvider(typeof(LocalFileSettingsProvider))]",
                     "ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal)" })
            Assert.Matches(LocalAppData, text);
        Assert.DoesNotMatch(LocalAppData, "Environment.SpecialFolder.CommonApplicationData");
        Assert.DoesNotMatch(LocalAppData, "Environment.SpecialFolder.ApplicationData");
        Assert.DoesNotMatch(LocalAppData, "Path.Combine(profile, \".local\", \"share\", \"claude\")");
    }

    [Fact]
    public void OnWindowsTheDataFolderIsTheOneUnderProgramData()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = AppPaths.DefaultDataRoot();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PoolWatch"), root);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\') + "\\";
        Assert.False(root.StartsWith(local, StringComparison.OrdinalIgnoreCase));
    }
}
