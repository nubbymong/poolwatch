using PoolWatch.Core.Setup;

namespace PoolWatch.Core.Recommendations;

/// <summary>
/// How to let Microsoft Defender scan a Dev Drive in performance mode, as instructions only: PoolWatch never changes
/// Defender settings. Checked against Microsoft Learn, "Set up a Dev Drive on Windows 11"
/// (https://learn.microsoft.com/windows/dev-drive/), "fsutil devdrv"
/// (https://learn.microsoft.com/windows-server/administration/windows-commands/fsutil-devdrv) and "Protect Dev Drive using
/// performance mode" (https://learn.microsoft.com/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode):
/// on a trusted Dev Drive real-time protection scans asynchronously, after the open completes ("open now, scan later"); it
/// needs real-time protection on; PerformanceModeStatus 0 is enabled (the default), 1 disabled; a trust change on a drive
/// in use takes effect the next time the drive mounts; a folder exclusion blocks scans altogether.
/// </summary>
public static class DefenderAdvice
{
    /// <summary>The prefix of every command line in <see cref="Text"/>, so a window can copy just the commands.</summary>
    public const string CommandIndent = "    ";

    /// <summary>
    /// The instructions for these Dev Drives ("D:\", "E:\"): their letters stand in every command. Only drive roots are used,
    /// so a command the user pastes into an administrator PowerShell never carries anything else.
    /// </summary>
    public static string Text(IReadOnlyList<string> devDriveRoots)
    {
        var letters = devDriveRoots.Where(EnvironmentFacts.IsDriveRoot).Select(EnvironmentFacts.Letter).DefaultIfEmpty("D:").ToList();
        string first = letters[0];
        var lines = new List<string>
        {
            "Microsoft Defender's performance mode for your Dev Drive",
            "",
            "On a trusted Dev Drive, Defender checks a file just after it opens instead of while it opens (Microsoft calls this \"open now, " +
            "scan later\"), so builds and AI coding tools that touch thousands of files wait much less. Protection stays on, and every " +
            "other drive is scanned as before.",
            "",
            "The trade-off: on that drive, a harmful file is caught a moment after it opens, not before. Trust only a Dev Drive that holds " +
            "your own code, packages and build output, never downloads.",
            "",
            "PoolWatch never changes Defender settings. To do it yourself, open PowerShell as administrator.",
            "",
            "Is the drive a Dev Drive, and is it trusted?",
        };
        lines.AddRange(letters.Select(l => $"{CommandIndent}fsutil devdrv query {l}"));
        lines.Add("Trust it. If the drive is in use, this takes effect the next time it is mounted, for example after a restart.");
        lines.AddRange(letters.Select(l => $"{CommandIndent}fsutil devdrv trust {l}"));
        lines.AddRange(
        [
            "Is performance mode on? 0 means on, 1 means off.",
            $"{CommandIndent}Get-MpPreference | Select-Object PerformanceModeStatus",
            "Turn it on (it needs real-time protection on, which is Defender's normal setting).",
            $"{CommandIndent}Set-MpPreference -PerformanceModeStatus Enabled",
            "",
            $"Excluding a folder (Add-MpPreference -ExclusionPath '{first}\\src') is a bigger step: Defender then never scans it at all. " +
            "Consider it only if performance mode is not enough, and only for a folder with nothing but your own code.",
            "",
            "More in Microsoft's guides \"Set up a Dev Drive on Windows 11\" and \"Protect Dev Drive using performance mode\".",
        ]);
        return string.Join(Environment.NewLine, lines);
    }
}
