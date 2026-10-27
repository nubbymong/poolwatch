using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>Review Part 3, addendum 4 C: cost per scheduled task / startup entry, and where startup entries live.</summary>
public class TasksPartThreeTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Adobe\\ARM\\1.0\\AdobeARM.exe\" /PRODUCT:Reader", @"C:\Program Files\Adobe\ARM\1.0\AdobeARM.exe")]
    [InlineData(@"C:\Program Files\CyberLink\CLToast\CLToast.exe /run", @"C:\Program Files\CyberLink\CLToast\CLToast.exe")]
    [InlineData(@"C:\Program Files\Logi\LogiDA.exe", @"C:\Program Files\Logi\LogiDA.exe")]
    [InlineData("rundll32.exe C:\\x\\y.dll,Entry", "rundll32.exe")]
    [InlineData("notepad", "notepad")]
    public void ProgramOfACommand(string command, string exe) => Assert.Equal(exe, EntryCost.ExeOf(command));

    [Fact]
    public void EnvironmentVariablesAreExpandedAndEmptyIsNone()
    {
        Assert.Equal(@"C:\Program Files\NVIDIA\nvtray.exe", EntryCost.ExeOf(@"%ProgramFiles%\NVIDIA\nvtray.exe -x", s => s.Replace("%ProgramFiles%", @"C:\Program Files")));
        Assert.Null(EntryCost.ExeOf("  "));
        Assert.Null(EntryCost.ExeOf(null));
    }

    [Fact]
    public void MatchesRunningProcessesByImageAndAddsTheirCost()
    {
        var live = new List<LiveImage>
        {
            new(new ProcessKey(10, 1), "CLToast.exe", @"C:\Program Files\CyberLink\CLToast\CLToast.exe"),
            new(new ProcessKey(11, 1), "CLToast.exe", @"C:\Program Files\CyberLink\CLToast\CLToast.exe"),
            new(new ProcessKey(12, 1), "CLToast.exe", @"D:\Other\CLToast.exe"),            // same name, another program
            new(new ProcessKey(13, 1), "LogiDA.exe", null),                                // path unreadable: matched by name
        };
        (double, long)? Cost(ProcessKey k) => k.Pid switch { 10 => (0.7, 2000), 11 => (0.5, 1000), 12 => (3.0, 0), 13 => (0.2, 0), _ => null };

        var toast = EntryCost.Match(@"C:\Program Files\CyberLink\CLToast\CLToast.exe", live, Cost);
        Assert.NotNull(toast);
        Assert.Equal(2, toast!.Processes);
        Assert.Equal(1.2, toast.Cores, 6);
        Assert.Equal("1.2 cores, 3k page faults/s (2 processes)", EntryCost.Text(@"C:\x\CLToast.exe", toast));

        Assert.Equal(1, EntryCost.Match(@"C:\Program Files\Logi\LogiDA.exe", live, Cost)!.Processes);
        Assert.Null(EntryCost.Match(@"C:\Program Files\HP\Doctor.exe", live, Cost));
        Assert.Equal("not running", EntryCost.Text(@"C:\Program Files\HP\Doctor.exe", null));
    }

    [Fact]
    public void ALauncherIsNotMeasurable()
    {
        Assert.True(EntryCost.ViaLauncher("rundll32.exe"));
        Assert.True(EntryCost.ViaLauncher(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"));
        Assert.False(EntryCost.ViaLauncher(@"C:\Program Files\CyberLink\CLToast\CLToast.exe"));
        Assert.Equal("not measurable (runs through cmd.exe)", EntryCost.Text(@"C:\Windows\System32\cmd.exe", null));
    }

    [Fact]
    public void StartupCoversBothViewsAndBothStartupFolders()
    {
        var all = StartupLocation.All;
        Assert.Contains(all, l => l.Label == "HKCU Run" && !l.Machine && l.ApprovedKey.EndsWith(@"StartupApproved\Run"));
        Assert.Contains(all, l => l.RunKey is { } r && r.Contains("WOW6432Node") && l.ApprovedKey.EndsWith(@"StartupApproved\Run32"));
        Assert.Equal(2, all.Count(l => l.StartupFolder && l.ApprovedKey.EndsWith(@"StartupApproved\StartupFolder")));
        Assert.Contains(all, l => l.StartupFolder && l.Machine);
        Assert.Contains(all, l => l.StartupFolder && !l.Machine);
        Assert.Equal(all.Count, all.Select(l => l.Label).Distinct().Count()); // labels are the keys the page sends back
        Assert.NotNull(StartupLocation.Find("HKLM Run (32-bit)"));
        Assert.Null(StartupLocation.Find("HKLM RunOnce"));
    }
}
