using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>S4: an elevated PoolWatch runs only from an administrator-only folder (pure path check, nothing is run).</summary>
public class InstallGuardTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Fact]
    public void OnlyProgramFilesCountsNotWindows()
    {
        // Review round 3: %WINDIR% has folders every user may write to (Windows\Temp, \Tasks, \tracing).
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.DoesNotContain(InstallGuard.AdminRoots(), r => r.Equals(windows, StringComparison.OrdinalIgnoreCase));
        foreach (var sub in new[] { "Temp", "Tasks", "tracing", "System32" })
            Assert.False(InstallGuard.IsTrustedInstallPath(Path.Combine(windows, sub, "PoolWatch.exe")), sub);
        Assert.True(InstallGuard.IsTrustedInstallPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PoolWatch", "PoolWatch.exe")));
    }

    [Theory]
    [InlineData(@"C:\Program Files\PoolWatch\PoolWatch.exe")]
    [InlineData(@"c:\program files\poolwatch\PoolWatch.exe")]
    [InlineData(@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe")]
    [InlineData(@"C:\Program Files\PoolWatch\..\PoolWatch\PoolWatch.exe")]
    public void AdminOnlyFoldersAreAccepted(string exe) => Assert.True(InstallGuard.IsUnderAdminRoot(exe, Roots));

    [Theory]
    [InlineData(@"C:\Users\owner\OneDrive\Desktop\PoolWatch.exe")]
    [InlineData(@"C:\Users\owner\AppData\Local\Temp\PoolWatch.exe")]
    [InlineData(@"C:\Program Files Evil\PoolWatch.exe")]          // a sibling whose name starts like the root
    [InlineData(@"C:\Program FilesX\PoolWatch.exe")]
    [InlineData(@"C:\Program Files\..\Users\owner\PoolWatch.exe")] // climbs out of the root
    [InlineData(@"D:\a\poolwatch\publish\fd\PoolWatch.exe")]
    public void UserWritableFoldersAreRefused(string exe) => Assert.False(InstallGuard.IsUnderAdminRoot(exe, Roots));

    [Fact]
    public void EmptyRootsAreIgnored() => Assert.False(InstallGuard.IsUnderAdminRoot(@"C:\x\PoolWatch.exe", ["", null!]));

    [Theory]
    [InlineData(true, new string[0])]                                              // the tray app
    [InlineData(true, new[] { "--tray" })]
    [InlineData(true, new[] { "--headless", "--duration", "60" })]                 // review round 2: headless too
    [InlineData(true, new[] { "--selftest", "--out", @"C:\x" })]
    [InlineData(true, new[] { "--service-off", "Fax" })]
    [InlineData(true, new[] { "--tasks-dump", @"C:\x\t.json" })]
    [InlineData(true, new[] { "--mark", "note" })]
    [InlineData(false, new[] { "--ui-smoke", "80" })]                              // the CI UI smoke
    [InlineData(false, new[] { "--token-leak-test", "100", @"C:\x\r.json" })]      // the guard's own helper
    [InlineData(false, new[] { "--headless", "--allow-any-location" })]            // CI / development, explicitly
    public void EveryModeIsCheckedUnlessExempt(bool applies, string[] args)
    {
        var opt = PoolWatch.Headless.CommandLineOptions.Parse(args);
        Assert.Empty(opt.Errors);
        Assert.Equal(applies, InstallGuard.Applies(opt));
    }
}
