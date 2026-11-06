using System.Collections;
using Microsoft.Win32;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The account's own environment, read as data (Engine/UserEnvironment.cs): PoolWatch runs with the short allowlist its
/// launcher builds, so a task's or startup app's command line and the account's TEMP folder are read from what Windows
/// gives the account's programs. The filling in is checked against Windows' own, and the reading against this PC's
/// registry, read only.
/// </summary>
public sealed class UserEnvironmentTests
{
    private static readonly Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ChocolateyInstall"] = @"C:\ProgramData\chocolatey", ["DriverData"] = @"C:\Windows\System32\Drivers\DriverData", ["A"] = "1",
    };

    [Theory]
    [InlineData(@"%ChocolateyInstall%\bin\choco.exe", @"C:\ProgramData\chocolatey\bin\choco.exe")]
    [InlineData(@"%driverdata%\x.exe", @"C:\Windows\System32\Drivers\DriverData\x.exe")] // names without regard to case
    [InlineData(@"%NotThere%\x.exe", @"%NotThere%\x.exe")]                                 // a name it does not hold stays
    [InlineData("%NotThere%A%", "%NotThere1")]                                              // whose closing % opens the next name
    [InlineData("100%%", "100%%")]
    [InlineData("%A%%A%", "11")]
    [InlineData("50% off", "50% off")]
    [InlineData("%A", "%A")]
    [InlineData("", "")]
    public void ANameIsFilledInAsWindowsDoes(string text, string expanded) => Assert.Equal(expanded, UserEnvironment.Expand(text, Vars));

    /// <summary>The same rules as Windows' ExpandEnvironmentStrings, over this process's own variables.</summary>
    [Theory]
    [InlineData(@"%SystemRoot%\System32")]
    [InlineData(@"%systemroot%%windir%")]
    [InlineData(@"%PoolWatchNoSuchVariable%\x")]
    [InlineData(@"%PoolWatchNoSuchVariable%SystemRoot%")]
    [InlineData(@"%%SystemRoot%%")]
    [InlineData(@"a%b%c%SystemRoot%")]
    [InlineData("%")]
    [InlineData("%%")]
    public void TheRulesAreWindowsOwn(string text)
    {
        var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) own[(string)e.Key] = (string)e.Value!;
        Assert.Equal(Environment.ExpandEnvironmentVariables(text), UserEnvironment.Expand(text, own));
    }

    /// <summary>
    /// Read from Windows for this account, never from this process: a variable this test process has that neither the
    /// machine's nor the account's registry sets (one the test runner set for itself) is not in it, while the account's
    /// own folders and the machine's variables are.
    /// </summary>
    [Fact]
    public void TheAccountsEnvironmentIsReadFromWindowsNotFromThisProcess()
    {
        var account = UserEnvironment.Read();
        Assert.NotEmpty(account);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), account["SystemRoot"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), account["USERPROFILE"], StringComparer.OrdinalIgnoreCase);
        Assert.True(account.ContainsKey("TEMP") && account.ContainsKey("Path") && account.ContainsKey("PROCESSOR_ARCHITECTURE"));
        Assert.DoesNotContain(account.Keys, k => k.StartsWith('='));
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        using var volatileEnv = Registry.CurrentUser.OpenSubKey("Volatile Environment");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { machine, user, volatileEnv })
            foreach (var name in key?.GetValueNames() ?? []) set.Add(name);
        string[] processOnly = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(n => n.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("VSTEST", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase))
            .Where(n => !set.Contains(n)).ToArray();
        Assert.NotEmpty(processOnly); // the test means something only when the runner set such a variable (the .NET SDK always does)
        foreach (var name in processOnly)
        {
            Assert.False(account.ContainsKey(name), $"{name} is this process's, not the account's");
            Assert.Equal($"%{name}%", UserEnvironment.Expander()($"%{name}%"));
        }
    }

    /// <summary>
    /// PoolWatch never fills in a %name% from its own environment (the launcher's short allowlist): every command line it
    /// shows is filled in from the account's (the Tasks and startup apps tab, the AI agent's list of the PC). The logon
    /// task's program is filled in from neither: a %name% in it makes the task untrusted (LogonTaskTests).
    /// </summary>
    [Fact]
    public void PoolWatchFillsInCommandLinesOnlyFromTheAccountsEnvironment()
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.False(File.ReadAllText(file).Contains("ExpandEnvironmentVariables", StringComparison.Ordinal), $"{Path.GetFileName(file)} fills in from PoolWatch's own environment");
        Assert.Contains("Entries(Need.Tasks.Tasks(), Need.Tasks.Startup(), Engine.UserEnvironment.Expander())", File.ReadAllText(Path.Combine(src, "Ui", "TasksTab.cs")));
        Assert.Contains("MainForm.CurrentRedaction(), UserEnvironment.Expander())", File.ReadAllText(Path.Combine(src, "Ui", "AgentDecisions.cs")));
    }

    [Fact]
    public void OneDriveFoldersComeFromTheAccountsEnvironment()
    {
        var account = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OneDrive"] = @"D:\Clouds\OneDrive-alice", ["OneDriveCommercial"] = @"D:\Clouds\OneDrive - Contoso" };
        var roots = Analysis.Diagnostics.OneDriveRoots(account);
        Assert.Contains(@"D:\Clouds\OneDrive-alice", roots);
        Assert.Contains(@"D:\Clouds\OneDrive - Contoso", roots);
    }

    [Fact]
    public void TheRedactionHidesTheAccountsOwnTempFolderAsWellAsPoolWatchs()
    {
        var account = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TEMP"] = @"D:\Scratch\alice-temp", ["TMP"] = @"D:\Scratch\alice-temp" };
        var redaction = Ui.MainForm.CurrentRedaction(account);
        Assert.Contains(@"D:\Scratch\alice-temp", redaction.TempPaths!);
        Assert.Single(redaction.TempPaths!, t => t.Equals(@"D:\Scratch\alice-temp", StringComparison.OrdinalIgnoreCase));
        if (Environment.GetEnvironmentVariable("TEMP") is { Length: > 3 } own) Assert.Contains(own, redaction.TempPaths!);
    }
}
