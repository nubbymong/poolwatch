using PoolWatch.Core.Recommendations;

namespace PoolWatch.Tests;

/// <summary>The Dev Drive advice for Microsoft Defender: every command, the trade-off, and the real drive letters.</summary>
public class DefenderAdviceTests
{
    private static List<string> CommandLines(string text) =>
        text.Split(Environment.NewLine).Where(l => l.StartsWith(DefenderAdvice.CommandIndent, StringComparison.Ordinal)).Select(l => l.Trim()).ToList();

    [Fact]
    public void HoldsEveryCommandForTheDrivesThatTriggeredIt()
    {
        string text = DefenderAdvice.Text([@"E:\", @"F:\"]);
        Assert.Equal(
        [
            "fsutil devdrv query E:", "fsutil devdrv query F:", "fsutil devdrv trust E:", "fsutil devdrv trust F:",
            "Get-MpPreference | Select-Object PerformanceModeStatus", "Set-MpPreference -PerformanceModeStatus Enabled",
        ], CommandLines(text));
        Assert.Contains("Add-MpPreference -ExclusionPath 'E:\\src'", text);
        Assert.DoesNotContain("D:", text);

        // Anything but a drive root is left out of the commands.
        Assert.Equal(CommandLines(DefenderAdvice.Text([@"E:\"])), CommandLines(DefenderAdvice.Text([@"E:\", @"F:\ and more text", @"\\server\share\"])));
    }

    [Fact]
    public void SaysTheTradeOffAndThatPoolWatchChangesNothing()
    {
        string text = DefenderAdvice.Text([@"D:\"]);
        Assert.Contains("a harmful file is caught a moment after it opens, not before", text);
        Assert.Contains("never downloads", text);
        Assert.Contains("PoolWatch never changes Defender settings", text);
        Assert.Contains("0 means on, 1 means off", text);
        Assert.Contains("the next time it is mounted", text);
        Assert.Contains("Defender then never scans it at all", text);
        Assert.DoesNotContain(CommandLines(text), c => c.StartsWith("Add-MpPreference", StringComparison.Ordinal)); // the exclusion is not one to copy
    }
}
