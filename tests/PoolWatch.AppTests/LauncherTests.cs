extern alias launcher;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using launcher::PoolWatch.Launcher;

namespace PoolWatch.AppTests;

/// <summary>
/// The launcher (src/PoolWatch.Launcher, the install folder's PoolWatch.exe), which every elevated PoolWatch starts through: the environment it
/// builds for PoolWatch (an allowlist filled in from Windows' own values, never the caller's environment), the command
/// line it passes the arguments on with, where and how it agrees to start, and how CI builds and ships it. Mostly pure,
/// with made-up folders; a few tests read the real folders, read only, and one starts cmd.exe through the launcher's own
/// CreateProcessW call to see the environment a real child gets.
/// </summary>
public sealed class LauncherTests
{
    private static readonly SystemPaths Sys = new(
        Windows: @"C:\Windows", System32: @"C:\Windows\system32", ProgramFiles: @"C:\Program Files", ProgramFilesX86: @"C:\Program Files (x86)",
        CommonProgramFiles: @"C:\Program Files\Common Files", CommonProgramFilesX86: @"C:\Program Files (x86)\Common Files",
        ProgramData: @"C:\ProgramData", Public: @"C:\Users\Public", UserProfile: @"C:\Users\alice", RoamingAppData: @"C:\Users\alice\AppData\Roaming",
        LocalAppData: @"C:\Users\alice\AppData\Local", UserName: "alice", UserDomain: "PC1", ComputerName: "PC1",
        NumberOfProcessors: "16", ProcessorArchitecture: "AMD64", ProcessorIdentifier: "Intel64 Family 6 Model 154 Stepping 3, GenuineIntel",
        ProcessorLevel: "6", ProcessorRevision: "9a03");

    private static string LauncherSource(string file) => File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch.Launcher", file));

    // ------------------------------------------------------------------ the environment

    [Fact]
    public void TheAllowlistIsKeptWithEveryValueFromWindowsAndTheProfile()
    {
        var env = EnvironmentBlock.Build(Sys);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows", ["windir"] = @"C:\Windows", ["SystemDrive"] = "C:", ["ComSpec"] = @"C:\Windows\system32\cmd.exe",
            ["Path"] = @"C:\Windows\system32;C:\Windows;C:\Windows\system32\Wbem;C:\Windows\system32\WindowsPowerShell\v1.0\",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC", ["OS"] = "Windows_NT",
            ["ProgramFiles"] = @"C:\Program Files", ["ProgramW6432"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["CommonProgramFiles"] = @"C:\Program Files\Common Files", ["CommonProgramW6432"] = @"C:\Program Files\Common Files",
            ["CommonProgramFiles(x86)"] = @"C:\Program Files (x86)\Common Files", ["ProgramData"] = @"C:\ProgramData", ["ALLUSERSPROFILE"] = @"C:\ProgramData",
            ["PUBLIC"] = @"C:\Users\Public", ["COMPUTERNAME"] = "PC1", ["USERPROFILE"] = @"C:\Users\alice", ["HOMEDRIVE"] = "C:", ["HOMEPATH"] = @"\Users\alice",
            ["APPDATA"] = @"C:\Users\alice\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\alice\AppData\Local",
            ["TEMP"] = @"C:\Users\alice\AppData\Local\Temp", ["TMP"] = @"C:\Users\alice\AppData\Local\Temp", ["USERNAME"] = "alice", ["USERDOMAIN"] = "PC1",
            // The machine's processor facts, from its registry.
            ["NUMBER_OF_PROCESSORS"] = "16", ["PROCESSOR_ARCHITECTURE"] = "AMD64", ["PROCESSOR_IDENTIFIER"] = "Intel64 Family 6 Model 154 Stepping 3, GenuineIntel",
            ["PROCESSOR_LEVEL"] = "6", ["PROCESSOR_REVISION"] = "9a03",
            [EnvironmentBlock.Marker] = "1",
        };
        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase), env);
    }

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("dotnet_startup_hooks")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("COR_PROFILER")]
    [InlineData("COR_PROFILER_PATH")]
    [InlineData("COMPlus_GCName")]
    [InlineData("complus_AltJit")]
    [InlineData("DOTNET_GCName")]
    [InlineData("DOTNET_GCPath")]
    [InlineData("DOTNET_JitPath")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("DOTNET_DbgEnableMiniDump")]
    [InlineData("DOTNET_DbgMiniDumpName")]
    [InlineData("DOTNET_DiagnosticPorts")]
    [InlineData("DOTNET_EnableEventPipe")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("DOTNET_ROOT_X64")]
    public void EveryDotNetStartUpVariableIsAKnobAndNoneIsBuilt(string name)
    {
        Assert.True(EnvironmentBlock.IsKnob(name));
        Assert.DoesNotContain(EnvironmentBlock.Build(Sys).Keys, EnvironmentBlock.IsKnob);
    }

    [Fact]
    public void EachPrefixIsAKnobWhateverItsCase()
    {
        Assert.Equal(["DOTNET_", "COMPlus_", "CORECLR_", "COR_"], EnvironmentBlock.KnobPrefixes);
        foreach (var prefix in EnvironmentBlock.KnobPrefixes)
        {
            Assert.True(EnvironmentBlock.IsKnob(prefix + "Anything"));
            Assert.True(EnvironmentBlock.IsKnob(prefix.ToLowerInvariant() + "x"));
        }
        Assert.False(EnvironmentBlock.IsKnob("CORE_ROOT"));
        Assert.False(EnvironmentBlock.IsKnob("COMPUTERNAME"));
        Assert.False(EnvironmentBlock.IsKnob(EnvironmentBlock.Marker));
    }

    /// <summary>
    /// No variable of the environment the launcher was started with is passed on or filled in: its code reads none, sets
    /// none, and makes none of the Windows calls that fill a value's variables in from the calling process's own
    /// environment (the shell's known-folder calls, which also keep their first answer for the rest of the process, and
    /// GetUserProfileDirectoryW); WindowsPaths.Read reads the registry's values as written; and Build takes Windows'
    /// values only. This holds whatever ran before in the same process, which a test that changes this process's own
    /// environment cannot show for a call the shell answers once per process.
    /// </summary>
    [Fact]
    public void TheLaunchersCodeReadsNoVariableOfItsOwnEnvironment()
    {
        string folder = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch.Launcher");
        // Its own files and every file its project compiles in from PoolWatch's program (AdminFolderRules.cs, ExitCodes.cs).
        var linked = Regex.Matches(LauncherSource("PoolWatch.Launcher.csproj"), @"<Compile Include=""\.\.\\PoolWatch\\([^""]+)"" Link=""[^""]+"" />")
            .Select(m => Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))).ToList();
        Assert.Equal(2, linked.Count);
        var files = Directory.GetFiles(folder, "*.cs").Concat(linked).ToList();
        Assert.Contains(files, f => f.EndsWith("EnvironmentBlock.cs", StringComparison.Ordinal));
        foreach (var file in files)
        {
            string code = File.ReadAllText(file);
            foreach (var call in new[] { "GetEnvironmentVariable", "SetEnvironmentVariable", "ExpandEnvironmentVariables", "GetEnvironmentStrings", "ExpandEnvironmentStrings",
                                         "GetFolderPath", "SpecialFolder", "SHGetKnownFolderPath", "SHGetFolderPath", "GetUserProfileDirectory", "GetProfilesDirectory" })
                Assert.False(code.Contains(call, StringComparison.Ordinal), $"{Path.GetFileName(file)} calls {call}");
        }
        // Every registry value is read as written: no GetValue call fills in its variables.
        string paths = LauncherSource("WindowsPaths.cs");
        Assert.Single(Regex.Matches(paths, @"\.GetValue\("));
        Assert.Contains("key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)", paths);
        Assert.Equal([typeof(SystemPaths)], typeof(EnvironmentBlock).GetMethod(nameof(EnvironmentBlock.Build))!.GetParameters().Select(p => p.ParameterType));
    }

    /// <summary>
    /// The profile folder and the AppData folders, from Windows' settings as written: only %SystemDrive% and %SystemRoot%
    /// (from the Windows folder) and %USERPROFILE% (the profile folder) are filled in, and a setting that names any other
    /// variable, which only the environment could fill in, leaves its folder out. With no setting, a folder is where
    /// Windows puts it.
    /// </summary>
    [Fact]
    public void TheProfileAndAppDataFoldersFillInOnlyWindowsOwnValues()
    {
        const string drive = "C:", windows = @"C:\Windows", profile = @"C:\Users\alice";
        Assert.Equal(@"C:\Users\alice", WindowsPaths.Profile(@"C:\Users\alice", drive, windows));
        Assert.Equal(@"C:\Users\alice", WindowsPaths.Profile(@"%SystemDrive%\Users\alice", drive, windows));
        foreach (var other in new[] { @"%USERPROFILE%", @"%HOMEDRIVE%\Users\alice", @"\\server\home\alice", "", null })
            Assert.Equal("", WindowsPaths.Profile(other, drive, windows));

        string Roaming(string? setting, string forProfile = profile) =>
            WindowsPaths.UserFolder(setting, WindowsPaths.RoamingStandard, forProfile, drive, windows);
        Assert.Equal(@"C:\Users\alice\AppData\Roaming", Roaming(@"%USERPROFILE%\AppData\Roaming"));
        Assert.Equal(@"C:\Users\alice\AppData\Roaming", Roaming(@"%userprofile%\AppData\Roaming"));
        Assert.Equal(@"C:\Users\alice\AppData\Roaming", Roaming(null));                   // no setting: where Windows puts it
        Assert.Equal(@"C:\Users\alice\AppData\Local", WindowsPaths.UserFolder(null, WindowsPaths.LocalStandard, profile, drive, windows));
        Assert.Equal(@"D:\Redirected\Roaming", Roaming(@"D:\Redirected\Roaming"));          // a folder redirected to another drive
        Assert.Equal(@"C:\Roaming\alice", Roaming(@"%SystemDrive%\Roaming\alice"));
        foreach (var other in new[] { @"%APPDATA%", @"%HOMEDRIVE%%HOMEPATH%\AppData\Roaming", @"%USERPROFILE%\%USERNAME%", @"%TEMP%\x",
                                      @"\\server\share\alice\AppData", @"AppData\Roaming" })
            Assert.Equal("", Roaming(other));
        Assert.Equal("", Roaming(@"%USERPROFILE%\AppData\Roaming", forProfile: ""));      // no profile folder: no AppData either
        Assert.Equal(("AppData", "Local AppData"), (WindowsPaths.RoamingSetting, WindowsPaths.LocalSetting));
        Assert.Equal((@"%USERPROFILE%\AppData\Roaming", @"%USERPROFILE%\AppData\Local"), (WindowsPaths.RoamingStandard, WindowsPaths.LocalStandard));
    }

    /// <summary>
    /// The filter on .NET's start-up variables is in <see cref="EnvironmentBlock.Add"/>, which every variable of the
    /// allowlist goes through: one put on the allowlist by mistake would still be left out.
    /// </summary>
    [Fact]
    public void AStartUpVariableIsRefusedEvenIfTheAllowlistNamedIt()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", "dotnet_gcName", "COMPlus_JitPath", "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING" })
            EnvironmentBlock.Add(env, name, @"C:\x\y.dll");
        Assert.Empty(env);
        EnvironmentBlock.Add(env, "", "x");
        EnvironmentBlock.Add(env, "A=B", "x");
        EnvironmentBlock.Add(env, "A\0B", "x");
        EnvironmentBlock.Add(env, "EMPTY", "");
        EnvironmentBlock.Add(env, "NUL", "a\0b");
        Assert.Empty(env);
        EnvironmentBlock.Add(env, "SystemRoot", @"C:\Windows");
        Assert.Equal(@"C:\Windows", Assert.Single(env).Value);
        // And Build puts every variable in through it.
        string build = MainWindowLayoutTests.Block(LauncherSource("EnvironmentBlock.cs"), "public static SortedDictionary<string, string> Build(SystemPaths sys)");
        Assert.Contains("void Set(string name, string? value) => Add(env, name, value);", build);
        Assert.DoesNotContain("env[", build);
    }

    [Fact]
    public void PathHoldsOnlyWindowsOwnFolders()
    {
        var env = EnvironmentBlock.Build(Sys);
        var path = env["Path"].Split(';');
        Assert.Equal([@"C:\Windows\system32", @"C:\Windows", @"C:\Windows\system32\Wbem", @"C:\Windows\system32\WindowsPowerShell\v1.0\"], path);
        Assert.All(path, p => Assert.StartsWith(Sys.Windows + @"\", p + @"\"));
        Assert.Single(env.Keys, k => k.Equals("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AValueThatCouldNotBeReadIsLeftOutNeverGuessed()
    {
        var sys = Sys with
        {
            ProgramFiles = "", ProgramData = @"Users\x", LocalAppData = "", Windows = "", UserProfile = @"\\server\home\alice",
            NumberOfProcessors = "", ProcessorIdentifier = "x\0y",
        };
        var env = EnvironmentBlock.Build(sys);
        foreach (var name in new[] { "ProgramFiles", "ProgramW6432", "ProgramData", "ALLUSERSPROFILE", "LOCALAPPDATA", "TEMP", "TMP", "SystemRoot",
                                     "windir", "SystemDrive", "ComSpec", "Path", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "NUMBER_OF_PROCESSORS",
                                     "PROCESSOR_IDENTIFIER" })
            Assert.DoesNotContain(name, env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Program Files (x86)", env["ProgramFiles(x86)"]);
        Assert.Equal("AMD64", env["PROCESSOR_ARCHITECTURE"]);
    }

    [Fact]
    public void TheBlockIsWhatCreateProcessReadsSortedAndEndedByTwoNuls()
    {
        var env = EnvironmentBlock.Build(Sys);
        string block = EnvironmentBlock.Serialize(env);
        Assert.EndsWith("\0\0", block);
        var entries = block[..^2].Split('\0');
        Assert.Equal(env.Count, entries.Length);
        Assert.Equal(entries.OrderBy(e => e.Split('=')[0], StringComparer.OrdinalIgnoreCase), entries);
        Assert.Contains(@"SystemRoot=C:\Windows", entries);
        Assert.Equal("A=1\0b=2\0\0", EnvironmentBlock.Serialize([new("A", "1"), new("b", "2")]));
        Assert.Equal("\0\0", EnvironmentBlock.Serialize([]));
    }

    [Fact]
    public void AMachineValueIsFilledInOnlyFromWindowsOwnFolder()
    {
        Assert.Equal(@"C:\ProgramData", WindowsPaths.Expand(@"%SystemDrive%\ProgramData", "C:", @"C:\Windows"));
        Assert.Equal(@"C:\Windows\x", WindowsPaths.Expand(@"%systemroot%\x", "C:", @"C:\Windows"));
        Assert.Equal("", WindowsPaths.Expand(@"%USERPROFILE%\x", "C:", @"C:\Windows")); // never from the environment
        Assert.Equal("", WindowsPaths.Expand(null, "C:", @"C:\Windows"));
        Assert.Equal("AMD64", WindowsPaths.Plain("AMD64"));
        Assert.Equal("", WindowsPaths.Plain("%PROCESSOR_ARCHITECTURE%"));
        Assert.Equal("", WindowsPaths.Plain(null));
    }

    [Fact]
    public void ThisPcsValuesAreReadFromWindows()
    {
        // Read only: the kernel's Windows folder, the machine's registry and the known folders.
        var sys = WindowsPaths.Read();
        Assert.True(Path.IsPathFullyQualified(sys.Windows), sys.Windows);
        Assert.StartsWith(sys.Windows + @"\", sys.System32 + @"\", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), sys.ProgramFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), sys.ProgramData, StringComparer.OrdinalIgnoreCase);
        // The processor facts as the machine's registry has them (where Windows itself takes them from).
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(WindowsPaths.MachineEnvironmentKey)!;
        Assert.Equal(machine.GetValue("PROCESSOR_ARCHITECTURE") as string, sys.ProcessorArchitecture);
        Assert.Equal(machine.GetValue("NUMBER_OF_PROCESSORS") as string, sys.NumberOfProcessors);
        Assert.True(int.Parse(sys.NumberOfProcessors) > 0);
        var env = EnvironmentBlock.Build(sys);
        Assert.Equal(sys.Windows, env["SystemRoot"]);
        Assert.True(env.ContainsKey("USERPROFILE") && env.ContainsKey("TEMP") && env.ContainsKey("PROCESSOR_ARCHITECTURE"));
    }

    /// <summary>
    /// The launcher's own CreateProcessW call (Native.Run), with the block built from this PC's values: a cmd.exe child
    /// writes the environment it got. It gets exactly that block (and the PROMPT cmd.exe sets for itself), none of this
    /// test process's own variables, which a child inherits when no block is passed or when Windows does not read it as
    /// one (CREATE_UNICODE_ENVIRONMENT). One value may differ, and only when this test process runs under emulation (an
    /// x64 test run on an ARM64 PC): PROCESSOR_ARCHITECTURE, which Windows then sets to the one it gives this process.
    /// </summary>
    [Fact]
    public void ARealChildGetsExactlyTheBlockAndNothingOfItsParent()
    {
        var sys = WindowsPaths.Read();
        var expected = EnvironmentBlock.Build(sys);
        var parent = Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();
        var parentOnly = parent.Where(n => !expected.ContainsKey(n) && !n.Equals("PROMPT", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(parentOnly); // the test means something only when the parent has variables the block does not
        string dir = Directory.CreateTempSubdirectory("pw-launcher-child-").FullName;
        try
        {
            string output = Path.Combine(dir, "env.txt");
            string cmd = Path.Combine(sys.System32, "cmd.exe");
            // /u: cmd.exe writes the list as UTF-16, so every value reads back exactly.
            string line = launcher::PoolWatch.Launcher.CommandLine.Build(cmd, ["/u", "/d", "/c", "set", ">", output])!;
            Assert.True(launcher::PoolWatch.Launcher.Native.Run(cmd, line, EnvironmentBlock.Serialize(expected), dir, out int exit, out string? error), error);
            Assert.Equal(0, exit);
            var got = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in File.ReadAllText(output, Encoding.Unicode).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                if (entry.IndexOf('=') is > 0 and var eq) got[entry[..eq]] = entry[(eq + 1)..];
            // Names only in the messages: the parent's values (a test runner's) are never written out.
            var extra = got.Keys.Where(n => !expected.ContainsKey(n) && !n.Equals("PROMPT", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(extra.Count == 0, "the child got variables the launcher did not give it: " + string.Join(", ", extra));
            var missing = expected.Keys.Where(n => !got.ContainsKey(n)).ToList();
            Assert.True(missing.Count == 0, "the child lacks variables the launcher gave it: " + string.Join(", ", missing));
            // Under emulation Windows gives the child the PROCESSOR_ARCHITECTURE it gives this process, not the machine's.
            const string arch = "PROCESSOR_ARCHITECTURE";
            bool emulated = RuntimeInformation.OSArchitecture != RuntimeInformation.ProcessArchitecture;
            string? ours = Environment.GetEnvironmentVariable(arch);
            var changed = expected.Where(e => got[e.Key] != e.Value && !(emulated && e.Key.Equals(arch, StringComparison.OrdinalIgnoreCase) && got[e.Key] == ours))
                .Select(e => e.Key).ToList();
            Assert.True(changed.Count == 0, "the child got other values for: " + string.Join(", ", changed) +
                $" ({arch}: given {expected.GetValueOrDefault(arch)}, got {got.GetValueOrDefault(arch)}, this process {ours}, emulated {emulated})");
            Assert.Equal("1", got[EnvironmentBlock.Marker]);
            Assert.DoesNotContain(got.Keys, EnvironmentBlock.IsKnob);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The launcher's own CreateProcessW call starts the child in the folder it is given (the launcher gives PoolWatch's
    /// own app folder, Program.AppFolderIn), not in the launcher's current folder, which goes to PoolWatch in the block
    /// instead (EnvironmentBlock.StartedIn): a cmd.exe child writes its current folder.
    /// </summary>
    [Fact]
    public void ARealChildStartsInTheFolderTheLauncherGivesIt()
    {
        string dir = Directory.CreateTempSubdirectory("pw-launcher-cwd-").FullName;
        try
        {
            string start = Directory.CreateDirectory(Path.Combine(dir, "app")).FullName, output = Path.Combine(dir, "cd.txt");
            Assert.NotEqual(Environment.CurrentDirectory.TrimEnd('\\'), start, StringComparer.OrdinalIgnoreCase);
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string line = launcher::PoolWatch.Launcher.CommandLine.Build(cmd, ["/u", "/d", "/c", "cd", ">", output])!;
            var env = EnvironmentBlock.Build(Sys with { Windows = Path.GetDirectoryName(Environment.SystemDirectory)!, System32 = Environment.SystemDirectory });
            Assert.True(launcher::PoolWatch.Launcher.Native.Run(cmd, line, EnvironmentBlock.Serialize(env), start, out int exit, out string? error), error);
            Assert.Equal(0, exit);
            Assert.Equal(start, File.ReadAllText(output, Encoding.Unicode).Trim(), StringComparer.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
        Assert.Equal(@"C:\Program Files\PoolWatch\app", launcher::PoolWatch.Launcher.Program.AppFolderIn(@"C:\Program Files\PoolWatch"));
        Assert.Equal(Path.GetDirectoryName(launcher::PoolWatch.Launcher.Program.ProgramIn(@"C:\Program Files\PoolWatch")),
            launcher::PoolWatch.Launcher.Program.AppFolderIn(@"C:\Program Files\PoolWatch"));
    }

    // ------------------------------------------------------------------ the command line

    private const string App = @"C:\Program Files\PoolWatch\PoolWatch.exe";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>How Windows' own parser splits a command line (the C runtime that PoolWatch.exe's host uses splits it the same way for these).</summary>
    private static string[] Split(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    public static TheoryData<string[]> Arguments() => new()
    {
        Array.Empty<string>(),
        new[] { "--tray" },
        new[] { "--out", @"C:\My Folder\" },                  // a space and a trailing backslash
        new[] { "--out", @"C:\My Folder\\" },                 // two trailing backslashes
        new[] { "--out", @"C:\NoSpace\" },                    // a trailing backslash with no space: not quoted, kept as it is
        new[] { "--mark", "a \"quoted\" note" },              // quotes inside
        new[] { "--mark", "\"" },                             // only a quote
        new[] { "--mark", @"back\""slash" },                  // a backslash before a quote
        new[] { "--mark", @"two\\""slashes" },                // two backslashes before a quote
        new[] { "--mark", @"\\server\share\dir" },            // backslashes not before a quote
        new[] { "--mark", "" },                               // an empty argument
        new[] { "--mark", "tab\there" },                      // a tab
        new[] { "--mark", " leading and trailing " },
        new[] { "--config", @"C:\x y\settings.json", "--interval", "5" },
    };

    [Theory]
    [MemberData(nameof(Arguments))]
    public void EveryArgumentReachesPoolWatchExactlyAsGiven(string[] args)
    {
        string line = launcher::PoolWatch.Launcher.CommandLine.Build(App, args)!;
        Assert.StartsWith($"\"{App}\"", line);
        Assert.Equal([App, .. args], Split(line));
    }

    [Theory]
    [InlineData("--tray", "--tray")]
    [InlineData(@"C:\My Folder\", "\"C:\\My Folder\\\\\"")]
    [InlineData(@"C:\NoSpace\", @"C:\NoSpace\")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    public void EachArgumentIsQuotedOnlyAsNeeded(string arg, string written)
    {
        var line = new StringBuilder();
        launcher::PoolWatch.Launcher.CommandLine.Append(line, arg);
        Assert.Equal(written, line.ToString());
    }

    [Fact]
    public void ACommandLineThatCannotBePassedOnSafelyIsRefused()
    {
        Assert.Null(launcher::PoolWatch.Launcher.CommandLine.Build("C:\\a\"b\\PoolWatch.exe", []));
        Assert.Null(launcher::PoolWatch.Launcher.CommandLine.Build(App, ["--mark", "a\0b"]));
        Assert.Null(launcher::PoolWatch.Launcher.CommandLine.Build(App, ["--mark", new string('x', launcher::PoolWatch.Launcher.CommandLine.MaxLength)]));
        Assert.NotNull(launcher::PoolWatch.Launcher.CommandLine.Build(App, ["--mark", new string('x', 1000)]));
    }

    [Fact]
    public void TheLongestCommandLineCreateProcessTakesIsPassedOnAndOneCharacterMoreIsNot()
    {
        // CreateProcessW takes at most 32767 characters with the ending NUL: 32766 of text.
        int fixedPart = $"\"{App}\" --mark ".Length;
        string? longest = launcher::PoolWatch.Launcher.CommandLine.Build(App, ["--mark", new string('x', 32_766 - fixedPart)]);
        Assert.Equal(32_766, longest?.Length);
        Assert.Null(launcher::PoolWatch.Launcher.CommandLine.Build(App, ["--mark", new string('x', 32_767 - fixedPart)]));
    }

    // ------------------------------------------------------------------ where and how it starts PoolWatch

    /// <summary>The launcher starts PoolWatch's own program, app\PoolWatch.exe below its own folder, and nothing else.</summary>
    [Fact]
    public void TheLauncherStartsTheProgramInItsAppFolder()
    {
        Assert.Equal(@"C:\Program Files\PoolWatch\app\PoolWatch.exe", launcher::PoolWatch.Launcher.Program.ProgramIn(@"C:\Program Files\PoolWatch"));
        Assert.Equal("app", launcher::PoolWatch.Launcher.Program.AppFolder);
        Assert.Equal("PoolWatch.exe", launcher::PoolWatch.Launcher.Program.ProgramName);
    }

    private const string Install = @"C:\Program Files\PoolWatch";
    private static readonly string Self = Install + @"\PoolWatch.exe", Program = Install + @"\app\PoolWatch.exe";

    private static string? Why(bool elevated = true, string folder = Install, SystemPaths? sys = null, Func<string, bool>? exists = null,
        Func<string, string?>? changeable = null) =>
        launcher::PoolWatch.Launcher.Program.WhyNotStart(elevated, folder, sys ?? Sys, Path.Combine(folder, "PoolWatch.exe"),
            launcher::PoolWatch.Launcher.Program.ProgramIn(folder), exists ?? (_ => true), changeable ?? (_ => null));

    [Fact]
    public void TheLauncherStartsPoolWatchOnlyWithAdministratorRightsAndOnlyFromProgramFiles()
    {
        Assert.Null(Why());
        Assert.Null(Why(folder: @"c:\program files (x86)\PoolWatch"));
        Assert.Null(Why(folder: @"C:\Program Files\Tools\PoolWatch\"));
        // Started without administrator rights (a compatibility setting, or UAC off for a standard account): said as that.
        Assert.Contains("without administrator rights", Why(elevated: false));
        // A copy anywhere else, where anyone could put a program named PoolWatch.exe below it.
        foreach (var elsewhere in new[] { @"C:\Users\alice\Downloads\PoolWatch", @"C:\Program Files", @"C:\Program Files Evil\PoolWatch", @"C:\ProgramData\PoolWatch",
                                          @"C:\Program Files\PoolWatch\..\..\Users\alice", @"\\server\share\Program Files\PoolWatch", @"D:\Program Files\PoolWatch", "PoolWatch" })
        {
            string? why = launcher::PoolWatch.Launcher.Program.WhyNotStart(true, elsewhere, Sys, Self, Program, _ => true, _ => null);
            Assert.True(why is not null && why.Contains(@"C:\Program Files\PoolWatch", StringComparison.Ordinal), $"{elsewhere}: {why}");
        }
        // Program Files that could not be read: nowhere is.
        Assert.NotNull(Why(sys: Sys with { ProgramFiles = "", ProgramFilesX86 = "" }));
        // No program below it.
        Assert.Contains(Program + " is not there", Why(exists: p => p != Program));
    }

    /// <summary>
    /// In Program Files, but in a folder a standard user can change (a folder some other program's setup opened up, say),
    /// or with a program a standard user can change: refused, so the launcher never lends its administrator prompt and
    /// signature to a program put there. Both the launcher and the program are judged, by the rules PoolWatch itself uses.
    /// </summary>
    [Fact]
    public void ALauncherOrAProgramAStandardUserCanChangeIsNeverStarted()
    {
        var judged = new List<string>();
        Assert.Null(Why(changeable: f => { judged.Add(f); return null; }));
        Assert.Equal([Self, Program], judged);
        string? why = Why(changeable: f => f == Program ? "The permissions of the folder let users without administrator rights change what is in it." : null);
        Assert.Contains("could change what this launcher starts as administrator", why);
        Assert.Contains("Install PoolWatch again", why);
        Assert.NotNull(Why(changeable: f => f == Self ? "x." : null));
        // What it says for each reason the shared rules give.
        var place = new launcher::PoolWatch.Engine.OpenPlace(Install + @"\app", Program, launcher::PoolWatch.Engine.OpenBecause.FolderRules);
        Assert.Contains(Install + @"\app", launcher::PoolWatch.Launcher.Program.Describe(place));
        Assert.Null(launcher::PoolWatch.Launcher.Program.Describe(null));
        foreach (var because in Enum.GetValues<launcher::PoolWatch.Engine.OpenBecause>())
            Assert.False(string.IsNullOrEmpty(launcher::PoolWatch.Launcher.Program.Describe(place with { Because = because })));
    }

    /// <summary>
    /// The launcher's Main: it finds where it is from the kernel, reads Windows' values, refuses (WhyNotStart, with the
    /// real rights, files and folder rules) before it builds anything or starts anything, then starts the program by its
    /// full path with the block it built.
    /// </summary>
    [Fact]
    public void TheLaunchersMainChecksWhereItIsBeforeItStartsAnything()
    {
        string main = Regex.Replace(MainWindowLayoutTests.Block(LauncherSource("Program.cs"), "private static int Main(string[] args)"), @"\s+", " ");
        string[] order =
        [
            "Native.AttachParentConsole();",
            "if (Native.OwnFinalPath() is not { } self || Path.GetDirectoryName(self) is not { } folder) return Fail(args,",
            "try { sys = WindowsPaths.Read(); }",
            "string app = ProgramIn(folder);",
            "if (WhyNotStart(Native.IsElevated(), folder, sys, self, app, File.Exists, ChangeableBy(sys)) is { } why) return Fail(args, why);",
            "var env = EnvironmentBlock.Build(sys);",
            "EnvironmentBlock.AddStartedIn(env, StartedFolder());",
            "string block = EnvironmentBlock.Serialize(env);",
            "if (CommandLine.Build(app, args) is not { } line) return Fail(args,",
            "return Native.Run(app, line, block, AppFolderIn(folder), out int exit, out string? error) ? exit : Fail(args, error!);",
        ];
        int at = -1;
        foreach (var step in order)
        {
            int next = main.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, $"not in this order in the launcher's Main: {step}");
            at = next;
        }
        Assert.Single(Regex.Matches(main, @"Native\.Run\("));
        // The folder rules come from the same source as the program's, read for Program Files as the registry names it.
        string changeable = Regex.Replace(MainWindowLayoutTests.Block(LauncherSource("Program.cs"), "private static Func<string, string?> ChangeableBy(SystemPaths sys)"), @"\s+", " ");
        Assert.Contains("new AdminPlaces([sys.ProgramFiles, sys.ProgramFilesX86], InstallGuard.ReadSecurity, Native.FolderFinalPathOf, InstallGuard.FolderEntries) " +
                        "{ AlwaysElevated = ElevatedAccounts.OfThisProcess(), };", changeable);
        Assert.Contains("<Compile Include=\"..\\PoolWatch\\Engine\\AdminFolderRules.cs\" Link=\"AdminFolderRules.cs\" />", LauncherSource("PoolWatch.Launcher.csproj"));
    }

    /// <summary>Where the launcher is, as the kernel names its open file, as a plain local path; a network path is not one.</summary>
    [Fact]
    public void TheLaunchersOwnPathIsTheKernelsLocalOne()
    {
        Assert.Equal(@"C:\Program Files\PoolWatch\PoolWatch.exe", launcher::PoolWatch.Launcher.Native.LocalPath(@"\\?\C:\Program Files\PoolWatch\PoolWatch.exe"));
        Assert.Equal(@"C:\x\y.exe", launcher::PoolWatch.Launcher.Native.LocalPath(@"C:\x\y.exe"));
        Assert.Null(launcher::PoolWatch.Launcher.Native.LocalPath(@"\\?\UNC\server\share\PoolWatch.exe"));
        Assert.Null(launcher::PoolWatch.Launcher.Native.LocalPath(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\x.exe"));
        // This test host's own file, through the same call: its long, real path.
        string? own = launcher::PoolWatch.Launcher.Native.OwnFinalPath();
        Assert.NotNull(own);
        Assert.Equal(new FileInfo(Environment.ProcessPath!).FullName, own, StringComparer.OrdinalIgnoreCase);
        // A file named another way (another case, a ".." on the way, its short 8.3 name where the volume keeps one) is
        // named as the kernel has it: so the Program Files check and the folder rules see the real folder.
        string dir = Directory.CreateTempSubdirectory("pw-launcher-final-").FullName;
        try
        {
            string file = Path.Combine(dir, "A Long File Name.txt");
            File.WriteAllText(file, "");
            string another = Path.Combine(dir, "sub", "..", "A LONG FILE NAME.TXT");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            string? final = launcher::PoolWatch.Launcher.Native.FinalPathOf(another);
            Assert.Equal(file, final, StringComparer.OrdinalIgnoreCase);
            Assert.EndsWith(@"\A Long File Name.txt", final, StringComparison.Ordinal); // the case it has on disk
            var shortName = new StringBuilder(1024);
            if (GetShortPathNameW(file, shortName, shortName.Capacity) is > 0 and < 1024 && !shortName.ToString().Equals(file, StringComparison.OrdinalIgnoreCase))
                Assert.Equal(file, launcher::PoolWatch.Launcher.Native.FinalPathOf(shortName.ToString()), StringComparer.OrdinalIgnoreCase);
            Assert.Null(launcher::PoolWatch.Launcher.Native.FinalPathOf(Path.Combine(dir, "not there.txt")));
            // A folder opens only with FILE_FLAG_BACKUP_SEMANTICS: the folder rules get where it really is (Program.ChangeableBy),
            // never nothing, which they would take for a folder whose place cannot be told.
            Assert.Null(launcher::PoolWatch.Launcher.Native.FinalPathOf(Path.Combine(dir, "sub")));
            Assert.Equal(dir, launcher::PoolWatch.Launcher.Native.FolderFinalPathOf(Path.Combine(dir, "sub", "..")), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(dir, "sub"), launcher::PoolWatch.Launcher.Native.FolderFinalPathOf(Path.Combine(dir, "SUB")), StringComparer.OrdinalIgnoreCase);
            Assert.Null(launcher::PoolWatch.Launcher.Native.FolderFinalPathOf(Path.Combine(dir, "not there")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);

    /// <summary>
    /// The account's profile, as the machine's profile list has it for this process's account, and its AppData folders, as
    /// the account's own User Shell Folders settings name them, filled in for that profile (read here independently; the
    /// shell's own known-folder call is no reference, as it keeps the first answer this test process got, which a parallel
    /// test may have asked for with another USERPROFILE in this process's environment).
    /// </summary>
    [Fact]
    public void TheProfileComesFromWindowsProfileList()
    {
        var sys = WindowsPaths.Read();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sys.UserProfile, StringComparer.OrdinalIgnoreCase);
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        using var list = Registry.LocalMachine.OpenSubKey(WindowsPaths.ProfileListKey + @"\" + me.User!.Value);
        Assert.Equal(Environment.ExpandEnvironmentVariables((string)list!.GetValue("ProfileImagePath")!), sys.UserProfile, StringComparer.OrdinalIgnoreCase);
        using var shell = Registry.CurrentUser.OpenSubKey(WindowsPaths.UserShellFoldersKey);
        string Setting(string name, string standard) => Environment.ExpandEnvironmentVariables(Regex.Replace(
            shell?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? standard, "%USERPROFILE%", sys.UserProfile.Replace("$", "$$"), RegexOptions.IgnoreCase));
        Assert.Equal(Setting("AppData", @"%USERPROFILE%\AppData\Roaming"), sys.RoamingAppData, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Setting("Local AppData", @"%USERPROFILE%\AppData\Local"), sys.LocalAppData, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePoolWatchItStartsGetsTheWindowStateHandlesAndShortcutOfTheLaunchersOwnStart()
    {
        var shortcut = new launcher::PoolWatch.Launcher.Native.STARTUPINFOW
        {
            dwFlags = launcher::PoolWatch.Launcher.Native.STARTF_USESHOWWINDOW | launcher::PoolWatch.Launcher.Native.STARTF_TITLEISLINKNAME | launcher::PoolWatch.Launcher.Native.STARTF_PREVENTPINNING | 0x0004,
            wShowWindow = 7, lpTitle = 0x1234, dwX = 50, lpDesktop = 0x99,
        };
        const nint own = 0x4242; // where the launcher's own AppUserModelID is
        var child = launcher::PoolWatch.Launcher.Native.ForChild(shortcut, own, out bool inherit);
        Assert.False(inherit);
        Assert.Equal(launcher::PoolWatch.Launcher.Native.STARTF_USESHOWWINDOW | launcher::PoolWatch.Launcher.Native.STARTF_TITLEISLINKNAME | launcher::PoolWatch.Launcher.Native.STARTF_PREVENTPINNING, child.dwFlags);
        Assert.Equal((short)7, child.wShowWindow);
        Assert.Equal(0x1234, child.lpTitle);
        Assert.Equal((0, 0), (child.dwX, (int)child.lpDesktop)); // no position or desktop
        Assert.Equal(Marshal.SizeOf<launcher::PoolWatch.Launcher.Native.STARTUPINFOW>(), child.cb);

        // Not started from a shortcut (a console, the logon task, Explorer): PoolWatch's windows get the launcher's
        // AppUserModelID and cannot be pinned, since a pin would name app\PoolWatch.exe itself.
        const int noPin = launcher::PoolWatch.Launcher.Native.STARTF_TITLEISAPPID | launcher::PoolWatch.Launcher.Native.STARTF_PREVENTPINNING;
        var console = new launcher::PoolWatch.Launcher.Native.STARTUPINFOW { dwFlags = launcher::PoolWatch.Launcher.Native.STARTF_USESTDHANDLES, hStdInput = 1, hStdOutput = 2, hStdError = 3, lpTitle = 0x55 };
        child = launcher::PoolWatch.Launcher.Native.ForChild(console, own, out inherit);
        Assert.True(inherit);
        Assert.Equal((launcher::PoolWatch.Launcher.Native.STARTF_USESTDHANDLES | noPin, 1, 2, 3, own), (child.dwFlags, (int)child.hStdInput, (int)child.hStdOutput, (int)child.hStdError, child.lpTitle));
        var task = launcher::PoolWatch.Launcher.Native.ForChild(new launcher::PoolWatch.Launcher.Native.STARTUPINFOW(), own, out _);
        Assert.Equal((noPin, own), (task.dwFlags, task.lpTitle));
        // A link name flag with no name is no shortcut either.
        var empty = launcher::PoolWatch.Launcher.Native.ForChild(new launcher::PoolWatch.Launcher.Native.STARTUPINFOW { dwFlags = launcher::PoolWatch.Launcher.Native.STARTF_TITLEISLINKNAME }, own, out _);
        Assert.Equal((noPin, own), (empty.dwFlags, empty.lpTitle));

        // Started with an AppUserModelID of the caller's: that one, still with no pinning.
        var appId = new launcher::PoolWatch.Launcher.Native.STARTUPINFOW { dwFlags = launcher::PoolWatch.Launcher.Native.STARTF_TITLEISAPPID, lpTitle = 0x77 };
        var withId = launcher::PoolWatch.Launcher.Native.ForChild(appId, own, out _);
        Assert.Equal((noPin, (nint)0x77), (withId.dwFlags, withId.lpTitle));
        Assert.Equal("PoolWatch.Monitor", launcher::PoolWatch.Launcher.Native.AppUserModelId);
        // The real start passes a pointer to that ID, held while CreateProcessW reads it.
        string run = Regex.Replace(MainWindowLayoutTests.Block(LauncherSource("Native.cs"),
            "public static bool Run(string program, string commandLine, string environment, string folder, out int exitCode, out string? error)"), @"\s+", " ");
        Assert.Contains("fixed (char* appId = AppUserModelId) { var si = ForChild(caller, (nint)appId, out bool handles); if (!CreateProcessW(", run);
    }

    [Fact]
    public void TheStartMenuEntryCarriesTheAppUserModelIdTheLauncherGivesEveryOtherStart()
    {
        // A PoolWatch the launcher starts any other way than from a shortcut (the logon task, Explorer) gets the
        // launcher's own AppUserModelID (ForChild, above), and one started from the Start menu entry gets the entry's: the
        // setup gives the entry that same one, so PoolWatch's windows and notices belong to one app however it was started,
        // the app a pin of that entry is. (How the taskbar then groups them can only be seen on a real desktop.)
        string iss = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "installer", "PoolWatch.iss")).Replace("\r\n", "\n");
        Assert.Contains("\nName: \"{autoprograms}\\PoolWatch\"; Filename: \"{app}\\PoolWatch.exe\"; AppUserModelID: \"" +
                        launcher::PoolWatch.Launcher.Native.AppUserModelId + "\"\n", iss);
        Assert.Single(Regex.Matches(iss, @"(?m)^Name: "));
    }

    [Fact]
    public void OnCtrlCTheLauncherKeepsWaitingForPoolWatchButAClosingConsoleEndsIt()
    {
        Assert.True(launcher::PoolWatch.Launcher.Native.KeepsWaitingOn(0));   // Ctrl+C
        Assert.True(launcher::PoolWatch.Launcher.Native.KeepsWaitingOn(1));   // Ctrl+Break
        foreach (uint ended in new uint[] { 2, 5, 6 })               // the console closing, a sign-out, a shutdown
            Assert.False(launcher::PoolWatch.Launcher.Native.KeepsWaitingOn(ended));
    }

    [Fact]
    public void OnlyAStartToUsePoolWatchShowsABoxWhenItCannotStart()
    {
        Assert.True(launcher::PoolWatch.Launcher.Program.ShowsABox([]));
        Assert.True(launcher::PoolWatch.Launcher.Program.ShowsABox(["--tray"]));
        Assert.True(launcher::PoolWatch.Launcher.Program.ShowsABox(["--TRAY"]));
        // The setup's own steps never wait for a click.
        Assert.False(launcher::PoolWatch.Launcher.Program.ShowsABox(["--stop-running"]));
        Assert.False(launcher::PoolWatch.Launcher.Program.ShowsABox(["--uninstall-cleanup"]));
        Assert.False(launcher::PoolWatch.Launcher.Program.ShowsABox(["--changes-dump", "x"]));
        Assert.False(launcher::PoolWatch.Launcher.Program.ShowsABox(["--tray", "--out", "x"]));
    }

    // ------------------------------------------------------------------ how CI builds, ships and signs it

    private static string Ci() => File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");

    /// <summary>
    /// The native build's link loads the launcher's DLLs from System32 only, and CI checks that flag in the built exe
    /// (the load configuration's DependentLoadFlags).
    /// </summary>
    [Fact]
    public void TheLaunchersDllsLoadFromSystem32Only()
    {
        Assert.Contains("<LinkerArg Include=\"/DEPENDENTLOADFLAG:0x800\" />", LauncherSource("PoolWatch.Launcher.csproj"));
        string ci = Ci();
        Assert.Contains("$loadConfig = [BitConverter]::ToUInt32($bytes, $optional + 112 + 10 * 8)", ci);
        Assert.Contains("$flags = [BitConverter]::ToUInt16($bytes, $at + 78)", ci);
        Assert.Contains("if ($flags -ne 0x800) { throw", ci);
    }

    /// <summary>
    /// The build job lays each build out as it is installed: PoolWatch itself published into app\, and the launcher,
    /// compiled to native code with nothing on its command line that changes what the project pins (start-up hooks, the
    /// GC, EventPipe), checked and copied in above it as PoolWatch.exe, before both builds are uploaded and zipped whole;
    /// every check and test that starts PoolWatch starts app\PoolWatch.exe. The sign job checks both programs of each
    /// build against the tag, signs and verifies them there and in the installer's stage, and puts the launcher into each
    /// zip under its second name too, PoolWatch-selfcontained.exe, the name the self-contained program had before 1.0.
    /// </summary>
    [Fact]
    public void CiPublishesTheLauncherNativeAndShipsAndSignsItInBothBuilds()
    {
        string ci = Ci();
        Assert.Contains("-p:SelfContained=false -p:PublishSingleFile=true -o publish/fd/app\n", ci);
        Assert.Contains("-p:EnableCompressionInSingleFile=true -o publish/sc/app\n", ci);
        Assert.DoesNotMatch(@"-o publish/(fd|sc)\s", ci);
        Assert.Contains("\n          dotnet publish src/PoolWatch.Launcher/PoolWatch.Launcher.csproj -c Release -r win-x64 -p:PublishAot=true -p:RestoreLockedMode=false " +
                        "\"-p:LauncherLinkMap=$PWD/publish/launcher-link.map\" -o publish/launcher\n", ci);
        // One publish of the launcher; the project is named once more only to resolve its compiler packages' licences.
        Assert.Single(Regex.Matches(ci, @"dotnet publish src/PoolWatch\.Launcher/PoolWatch\.Launcher\.csproj"));
        Assert.Equal(["publish", "msbuild"], Regex.Matches(ci, @"dotnet (?<verb>\w+) src/PoolWatch\.Launcher/PoolWatch\.Launcher\.csproj").Select(m => m.Groups["verb"].Value));
        Assert.Equal(2, Regex.Matches(ci, @"PoolWatch\.Launcher\.csproj").Count);
        foreach (var property in new[] { "StartupHookSupport", "IlcStandaloneGCSupport", "EventSourceSupport", "UseSystemResourceKeys", "IlcGenerateStackTraceData" })
            Assert.DoesNotContain(property, ci);
        // Its checks: native (no .dll or .json next to it), the manifest's administrator level, a Windows program, the build's version.
        Assert.Contains("$exe = 'publish/launcher/PoolWatch.exe'", ci);
        Assert.Contains("Where-Object { $_.Extension -in '.dll', '.json' }", ci);
        Assert.Contains("IndexOf('level=\"requireAdministrator\"') -lt 0) { throw", ci);
        Assert.Contains("if ($subsystem -ne 2) { throw", ci);
        Assert.Contains("if ($version -ne (Get-Item publish/fd/app/PoolWatch.exe).VersionInfo.ProductVersion) { throw", ci);
        // Into both builds, before they are uploaded and zipped whole.
        int copy = ci.IndexOf("foreach ($d in 'publish/fd', 'publish/sc') { Copy-Item $exe $d }", StringComparison.Ordinal);
        Assert.True(copy > 0, "the launcher is not copied into both builds");
        Assert.True(copy < ci.IndexOf("name: PoolWatch-exe", StringComparison.Ordinal), "the launcher is copied after the builds are uploaded");
        Assert.Contains("Compress-Archive -Path \"publish/$($pair[0])/*\"", ci);
        // Every PoolWatch the CI checks start is PoolWatch itself, below the launcher.
        Assert.DoesNotMatch(@"publish/(fd|sc)/PoolWatch(-selfcontained)?\.exe""", ci);
        Assert.Equal(6, Regex.Matches(ci, Regex.Escape("-Exe \"$PWD/publish/fd/app/PoolWatch.exe\"")).Count);
        Assert.Contains("\"POOLWATCH=$app\\app\\PoolWatch.exe\" >> $env:GITHUB_ENV", ci);
        // The sign job: the tag's version, at least four signed programs (the launcher and PoolWatch itself in each build),
        // and each verified, in the builds and in the installer's stage.
        Assert.Contains("foreach ($exe in 'publish/fd/PoolWatch.exe', 'publish/fd/app/PoolWatch.exe', 'publish/sc/PoolWatch.exe', 'publish/sc/app/PoolWatch.exe') {", ci);
        Assert.Contains("if ($signed -lt 4) { throw", ci);
        Assert.Contains("foreach ($exe in 'publish/fd/PoolWatch.exe', 'publish/fd/app/PoolWatch.exe', 'publish/sc/PoolWatch.exe', 'publish/sc/app/PoolWatch.exe',\n" +
                        "                           'setup/stage/PoolWatch.exe', 'setup/stage/app/PoolWatch.exe', \"setup/PoolWatch-$env:GITHUB_REF_NAME-setup-win-x64.exe\") {", ci);
        // Each zip carries the launcher under both names its program had before 1.0, copied after the installer is built
        // from that folder and every signature is verified, and before the zips are made.
        int copyNames = ci.IndexOf("foreach ($d in 'publish/fd', 'publish/sc') {\n            Copy-Item \"$d/PoolWatch.exe\" \"$d/PoolWatch-selfcontained.exe\"\n", StringComparison.Ordinal);
        Assert.True(copyNames > ci.IndexOf("name: Verify every signature", StringComparison.Ordinal) && copyNames < ci.IndexOf("Compress-Archive", StringComparison.Ordinal),
            "the launcher is not copied under its second name into both builds between the checks and the zips");
        Assert.Contains("if ((Get-FileHash \"$d/PoolWatch.exe\").Hash -ne (Get-FileHash \"$d/PoolWatch-selfcontained.exe\").Hash) { throw", ci);
        Assert.DoesNotContain("Rename-Item", ci);
        Assert.Equal(["PoolWatch.exe", "PoolWatch-selfcontained.exe"], Engine.LaunchGate.LauncherFileNames);
        Assert.DoesNotContain("PoolWatchLauncher", ci);
    }

    /// <summary>
    /// CI lists, from the linker's map, every library whose code the native launcher carries and every DLL it imports,
    /// and stops the build on a library THIRD-PARTY-NOTICES.md does not cover, on a Universal C Runtime linked in, or on a
    /// Visual C++ runtime DLL imported. The Visual C++ libraries it allows are the ones the notices name.
    /// </summary>
    [Fact]
    public void CiChecksTheLibrariesTheLaunchersLinkUsedAgainstTheNotices()
    {
        string ci = Ci();
        Assert.Contains("<LinkerArg Include=\"/MAP:&quot;$(LauncherLinkMap)&quot;\" Condition=\"'$(LauncherLinkMap)' != ''\" />", LauncherSource("PoolWatch.Launcher.csproj"));
        int publish = ci.IndexOf("-p:LauncherLinkMap=$PWD/publish/launcher-link.map", StringComparison.Ordinal);
        int check = ci.IndexOf("$map = 'publish/launcher-link.map'", StringComparison.Ordinal);
        Assert.True(publish > 0 && check > publish, "the link map is not written, then read");
        // CI's own two patterns, taken from ci.yml and read here with .NET's regex as PowerShell's -match reads them
        // (.NET's, without regard to case), over a link map in MSVC's layout: only the symbol lines after the column
        // header count. The header, the timestamp and load address, the section table, the entry point and the "Static
        // symbols" heading are never read as a library (the header's own last column, Lib:Object, once was).
        var header = new Regex(Regex.Match(ci, @"\$symbols = \$line -match '(?<p>[^']+)'; continue").Groups["p"].Value, RegexOptions.IgnoreCase);
        var symbol = new Regex(Regex.Match(ci, @"if \(\$line -match '(?<p>\^\\s\*\[0-9A-Fa-f\][^']+)'\) \{").Groups["p"].Value, RegexOptions.IgnoreCase);
        Assert.Contains("if (-not $symbols) { throw", ci);
        // PowerShell sets $Matches anew at every -match that succeeds, so the loop takes both captures before it tests
        // either (a -match on the object first left $Matches.obj empty and stopped the build).
        Assert.Contains("$lib = ($Matches.lib -replace '\\.lib$', '').ToLowerInvariant(); $obj = $Matches.obj.ToLowerInvariant()\n" +
                        "              if ($obj.EndsWith('.dll')) { $imports[$obj] = $true } else { $static[$lib] = $true }", ci);
        Assert.DoesNotMatch(@"\$Matches\.\w+ -match", ci);
        const string map = """
             PoolWatch

             Timestamp is 6543210a (Mon Nov  2 10:00:00 2026)

             Preferred load address is 0000000140000000

             Start         Length     Name                   Class
             0001:00000000 0007e8c0H .text                   CODE
             0002:00000000 00000120H .idata$5                DATA

              Address         Publics by Value              Rva+Base               Lib:Object

             0000:00000000       __guard_flags              0000000000000000     <absolute>
             0001:00000010       memcpy                     0000000140001010 f   libvcruntime:memcpy.obj
             0001:00002000       ?x@@YAXXZ                  0000000140003000 f   Runtime.WorkstationGC:gcenv.ee.obj
             0001:00004000       main                       0000000140005000 f   PoolWatch.obj
             0001:00004100       x                          0000000140005100 f   C:\b\x.obj
             0002:00000120       __imp_CloseHandle          0000000140080120     kernel32:KERNEL32.dll

             entry point at        0001:0007f0e0

             Static symbols

             0001:00007000       static_fn                  0000000140008000 f   libcmt:x.obj
            """;
        var read = new List<string>();
        bool symbols = false;
        foreach (var line in map.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (!symbols) { symbols = header.IsMatch(line); continue; }
            if (symbol.Match(line) is { Success: true } m) read.Add(m.Groups["lib"].Value + ":" + m.Groups["obj"].Value);
        }
        Assert.True(symbols);
        Assert.Equal(["libvcruntime:memcpy.obj", "Runtime.WorkstationGC:gcenv.ee.obj", "kernel32:KERNEL32.dll", "libcmt:x.obj"], read);
        // No line but a symbol line matches the symbol pattern on its own either.
        foreach (var line in new[] { "  Address         Publics by Value              Rva+Base               Lib:Object", " Timestamp is 6543210a (Mon Nov  2 10:00:00 2026)",
                                     " entry point at        0001:0007f0e0", " Static symbols", " Preferred load address is 0000000140000000",
                                     " 0001:00000000 0007e8c0H .text                   CODE", " 0000:00000000       __guard_flags              0000000000000000     <absolute>" })
            Assert.False(symbol.IsMatch(line), line);
        // Both builds carry the launcher, so both carry the licence and notice files of the NativeAOT compiler packages its
        // publish used (resolved for the launcher's own properties), copied before the builds are uploaded; a package
        // without either file stops the build. The installer's test looks for them in the install folder, and the notices
        // say where they are.
        int licences = ci.IndexOf("- name: Add the .NET runtime's licences to both builds", StringComparison.Ordinal);
        Assert.True(licences > publish && licences < ci.IndexOf("name: PoolWatch-exe", StringComparison.Ordinal), "the licences are not added after the launcher's publish and before the upload");
        Assert.Contains("dotnet msbuild src/PoolWatch.Launcher/PoolWatch.Launcher.csproj -t:ResolveFrameworkReferences -getItem:ResolvedILCompilerPack,ResolvedTargetILCompilerPack `\n" +
                        "            -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:PublishAot=true -p:RestoreLockedMode=false | ConvertFrom-Json).Items", ci);
        Assert.Contains("if (-not ($ilcFiles | Where-Object BaseName -eq $name)) { throw", ci);
        Assert.Contains("foreach ($d in 'publish/fd', 'publish/sc') {\n            $to = New-Item -ItemType Directory -Force \"$d/licenses/Microsoft.DotNet.ILCompiler\"", ci);
        string e2e = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "installer-e2e.ps1"));
        Assert.Contains(@"'licenses\Microsoft.DotNet.ILCompiler\LICENSE.TXT', 'licenses\Microsoft.DotNet.ILCompiler\THIRD-PARTY-NOTICES.TXT'", e2e);
        // The loop over those files, a statement of the script's top level, does only this for each: checks it and prints
        // its CHECK-RAN marker (CheckMarkerTests), so CI shows that the check ran.
        Assert.True(ScriptCode.OnlyCheckThenMarker(e2e, "Assert-True (Test-Path (Join-Path $app $f)) \"installed $f\"", "installer-installed-each-file",
            @"foreach ($f in 'PoolWatch.exe', 'app\PoolWatch.exe',"), "the installer test does not check each installed file with its marker next");
        Assert.Contains(@"`licenses\Microsoft.DotNet.ILCompiler\` (`LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`)",
            File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "THIRD-PARTY-NOTICES.md")));
        // And the two patterns are exactly these (so the samples above are what CI reads).
        Assert.Equal(@"^\s*Address\s+Publics by Value\s", header.ToString());
        Assert.Equal(@"^\s*[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}\s.*\s(?<lib>[A-Za-z][^\s:]+):(?<obj>\S+)\s*$", symbol.ToString());
        Assert.Contains("if ($other.Count -gt 0) { throw", ci);
        Assert.Contains("if ($static.ContainsKey('libucrt')) { throw", ci);
        Assert.Contains("if ($vcDlls.Count -gt 0) { throw", ci);
        var visualC = Regex.Match(ci, @"\$visualC = (?<list>'[^\r\n]*')").Groups["list"].Value.Split(',').Select(s => s.Trim().Trim('\'')).ToList();
        Assert.Contains("libcmt", visualC);
        string notices = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "THIRD-PARTY-NOTICES.md"));
        foreach (var lib in visualC)
            Assert.Contains($"`{lib}.lib`", notices);
        Assert.Contains("ucrtbase.dll", notices);
        Assert.Contains("uuid.lib", notices);
    }
}

/// <summary>The tests that change this process's own environment run on their own, after the parallel ones.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class LauncherOwnEnvironmentTests
{
    /// <summary>
    /// With hostile variables in the process's own environment (a profile, AppData folders, a system drive and folder and
    /// account names of an attacker's choosing, and .NET's start-up variables), WindowsPaths.Read gives exactly what it
    /// gives without them, and neither it nor the block holds any of them: every value comes from Windows' settings as
    /// written, never from this environment. Nothing it reads is kept per process, so this holds in any order of tests;
    /// the calls that would keep a first answer (the shell's known folders) are pinned out of the launcher's code by
    /// <see cref="LauncherTests.TheLaunchersCodeReadsNoVariableOfItsOwnEnvironment"/>.
    /// </summary>
    [Fact]
    public void AHostileEnvironmentOfItsOwnChangesNothingTheLauncherBuilds()
    {
        const string evil = @"C:\EvilPlace";
        var names = new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "SystemDrive", "SystemRoot", "windir", "ProgramFiles", "ProgramData",
                            "PUBLIC", "TEMP", "TMP", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
                            "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "COMPlus_GCName" };
        var before = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var clean = WindowsPaths.Read();
        try
        {
            foreach (var n in names) Environment.SetEnvironmentVariable(n, evil + @"\" + n);
            Environment.SetEnvironmentVariable("USERPROFILE", evil);
            Environment.SetEnvironmentVariable("SystemDrive", "Z:");
            var sys = WindowsPaths.Read();
            Assert.Equal(clean, sys);
            foreach (var v in new[] { sys.UserProfile, sys.RoamingAppData, sys.LocalAppData })
                Assert.False(v.Length == 0 || v.StartsWith(evil, StringComparison.OrdinalIgnoreCase) || v.StartsWith("Z:", StringComparison.OrdinalIgnoreCase), v);
            var env = EnvironmentBlock.Build(sys);
            Assert.DoesNotContain(env, e => e.Value.Contains(evil, StringComparison.OrdinalIgnoreCase) || e.Value.StartsWith("Z:", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env.Keys, EnvironmentBlock.IsKnob);
        }
        finally
        {
            foreach (var (n, v) in before) Environment.SetEnvironmentVariable(n, v);
        }
    }

    /// <summary>
    /// The folder the launcher passes on (<see cref="EnvironmentBlock.StartedIn"/>) is the one it was started in, its
    /// current folder, which PoolWatch resolves the paths it is given against: never its own folder (an install folder,
    /// where PoolWatch writes nothing). Run here in a folder that is neither this test's own nor its current one before.
    /// </summary>
    [Fact]
    public void TheFolderItPassesOnIsTheOneItWasStartedIn()
    {
        string before = Environment.CurrentDirectory;
        string elsewhere = Directory.CreateTempSubdirectory("pw-started-in-").FullName;
        try
        {
            Environment.CurrentDirectory = elsewhere;
            string? passed = launcher::PoolWatch.Launcher.Program.StartedFolder();
            Assert.Equal(elsewhere.TrimEnd('\\'), passed?.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(AppContext.BaseDirectory.TrimEnd('\\'), passed?.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = before;
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}
