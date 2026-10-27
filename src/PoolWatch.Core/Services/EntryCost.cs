using System.Globalization;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Services;

/// <summary>A live process as the cost match sees it.</summary>
public sealed record LiveImage(ProcessKey Key, string Image, string? ImagePath);

/// <summary>What a scheduled task or startup entry costs now: the processes running its program, added up.</summary>
public sealed record EntryCostResult(int Processes, double Cores, long PageFaultsPerSec);

/// <summary>
/// The cost of a scheduled task or a startup entry (addendum 4 C): the program it runs is matched against the live
/// processes by image (full path when both are known, else the file name), and their costs are added up. "Not running"
/// when nothing matches. A program that runs through a shared launcher (cmd, PowerShell, rundll32, wscript, ...) is not
/// measurable this way: every other use of that launcher would be counted too.
/// </summary>
public static class EntryCost
{
    /// <summary>
    /// The program a command line starts: a quoted first token, else everything up to the first ".exe" (unquoted paths
    /// with spaces are common in Run keys), else the first token; environment variables expanded. Null when empty.
    /// </summary>
    public static string? ExeOf(string? command, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = command.Trim();
        if (expand is not null) c = expand(c);
        string exe;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            exe = end > 0 ? c[1..end] : c[1..];
        }
        else
        {
            int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0 && (i + 4 == c.Length || c[i + 4] is ' ' or '\t' or ',')) exe = c[..(i + 4)];
            else { int sp = c.IndexOfAny([' ', '\t']); exe = sp > 0 ? c[..sp] : c; }
        }
        exe = exe.Trim();
        return exe.Length == 0 ? null : exe;
    }

    /// <summary>The program runs through a shared launcher whose other uses would be counted too.</summary>
    public static bool ViaLauncher(string? exe) =>
        exe is not null && ProcessIdentities.SharedRuntimes.Contains(FileName(exe));

    /// <summary>The live processes running <paramref name="exe"/>, with their costs added up; null when none runs.</summary>
    public static EntryCostResult? Match(string? exe, IEnumerable<LiveImage> live, Func<ProcessKey, (double cores, long faultsPerSec)?> cost)
    {
        if (exe is null) return null;
        string name = FileName(exe);
        bool full = exe.Contains('\\');
        int n = 0;
        double cores = 0;
        long faults = 0;
        foreach (var p in live)
        {
            bool same = full && p.ImagePath is { Length: > 0 } ip
                ? ip.Equals(exe, StringComparison.OrdinalIgnoreCase)
                : p.Image.Equals(name, StringComparison.OrdinalIgnoreCase);
            if (!same) continue;
            n++;
            if (cost(p.Key) is { } c) { cores += c.cores; faults += c.faultsPerSec; }
        }
        return n == 0 ? null : new EntryCostResult(n, cores, faults);
    }

    /// <summary>"not running", "idle (1 process)", "1.2 cores, 3k page faults/s (2 processes)", or why it is not measurable.</summary>
    public static string Text(string? exe, EntryCostResult? r)
    {
        if (exe is null) return "no program";
        if (ViaLauncher(exe)) return $"not measurable (runs through {FileName(exe)})";
        if (r is null) return "not running";
        return $"{ServicePlan.Cost(r.Cores, r.PageFaultsPerSec)} ({r.Processes.ToString(CultureInfo.InvariantCulture)} process{(r.Processes == 1 ? "" : "es")})";
    }

    private static string FileName(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}

/// <summary>
/// "End process" for a heavy background app (addendum 4 C) is offered only where ending it cannot take Windows down
/// (Part 3 review): never a core system process, a security process, a process that hosts a Windows service (turn the
/// service off on the services list instead: that is reversible) or a virtual machine's memory process.
/// </summary>
public static class EndProcessPolicy
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "MsMpEng.exe",
        "MsMpEngCP.exe", "NisSrv.exe", "SecurityHealthService.exe", "MpDefenderCoreService.exe", "audiodg.exe", "spoolsv.exe",
    };

    /// <summary>Null when the process may be ended (after the owner confirms); else why PoolWatch will not end it.</summary>
    public static string? WhyNot(int pid, string image, IReadOnlyCollection<string> hostedServices)
    {
        if (pid <= 4) return "it is part of Windows itself";
        if (Critical.Contains(image)) return $"{image} is a core Windows or security process: ending it can stop or restart Windows";
        if (hostedServices.Count > 0)
            return $"it runs the Windows service{(hostedServices.Count == 1 ? "" : "s")} {string.Join(", ", hostedServices.Take(5))}{(hostedServices.Count > 5 ? ", ..." : "")}: " +
                   "turn the service off on the Background services list instead (Restore puts it back)";
        if (image.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase))
            return "it is a virtual machine's memory: shut the VM (or WSL) down from Hyper-V Manager or wsl --shutdown instead";
        return null;
    }
}

/// <summary>
/// Where startup entries live and which Explorer StartupApproved key Task Manager keeps their enabled state in (addendum
/// 4 C): the Run keys of both registry views and the two Startup folders. A disable writes the same 12-byte value Task
/// Manager writes, so the owner can flip it back there.
/// </summary>
public sealed record StartupLocation(string Label, bool Machine, string? RunKey, bool StartupFolder, string ApprovedKey)
{
    public const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static readonly IReadOnlyList<StartupLocation> All =
    [
        new("HKCU Run", false, @"Software\Microsoft\Windows\CurrentVersion\Run", false, ApprovedRoot + "Run"),
        new("HKLM Run", true, @"Software\Microsoft\Windows\CurrentVersion\Run", false, ApprovedRoot + "Run"),
        // 32-bit programs' machine-wide Run key (the WOW64 view): Task Manager keeps their state under Run32.
        new("HKLM Run (32-bit)", true, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", false, ApprovedRoot + "Run32"),
        new("Startup folder (you)", false, null, true, ApprovedRoot + "StartupFolder"),
        new("Startup folder (all users)", true, null, true, ApprovedRoot + "StartupFolder"),
    ];

    public static StartupLocation? Find(string label) => All.FirstOrDefault(l => l.Label == label);
}
