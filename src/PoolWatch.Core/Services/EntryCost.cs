using System.Globalization;
using System.Text.RegularExpressions;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Services;

/// <summary>A live process as the cost match sees it.</summary>
public sealed record LiveImage(ProcessKey Key, string Image, string? ImagePath);

/// <summary>What a scheduled task or startup entry costs now: the processes running its program, added up.</summary>
public sealed record EntryCostResult(int Processes, double Cores, long PageFaultsPerSec)
{
    /// <summary>The processes that run the program, in the order they were met.</summary>
    public IReadOnlyList<ProcessKey> Keys { get; init; } = [];

    /// <summary>
    /// Every one of them was matched by its own full path, the one the command names. Only then may "End it now" end
    /// them: a match by file name alone can be another program of the same name.
    /// </summary>
    public bool ByFullPath { get; init; }

    /// <summary>
    /// The cost of at least one of them is known. Before PoolWatch's first sampling window no cost is: the cost then
    /// says "measuring", never "idle".
    /// </summary>
    public bool Measured { get; init; }
}

/// <summary>
/// The cost of a scheduled task or a startup entry (addendum 4 C): the program it runs is matched against the live
/// processes by image (full path when both are known, else the file name), and their costs are added up. "Not running"
/// when nothing matches. A program that runs through a shared launcher (cmd, PowerShell, rundll32, wscript, ...) is not
/// measurable this way: every other use of that launcher would be counted too.
/// </summary>
public static partial class EntryCost
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
        double cores = 0;
        long faults = 0;
        bool byPath = full, measured = false;
        var keys = new List<ProcessKey>();
        foreach (var p in live)
        {
            bool path = full && p.ImagePath is { Length: > 0 };
            bool same = path
                ? p.ImagePath!.Equals(exe, StringComparison.OrdinalIgnoreCase)
                : p.Image.Equals(name, StringComparison.OrdinalIgnoreCase);
            if (!same) continue;
            byPath &= path;
            keys.Add(p.Key);
            if (cost(p.Key) is { } c) { cores += c.cores; faults += c.faultsPerSec; measured = true; }
        }
        return keys.Count == 0 ? null : new EntryCostResult(keys.Count, cores, faults) { Keys = keys, ByFullPath = byPath, Measured = measured };
    }

    /// <summary>"not running", "idle (1 process)", "1.2 cores, 3k page faults/s (2 processes)", or why it is not measurable.</summary>
    public static string Text(string? exe, EntryCostResult? r)
    {
        if (exe is null) return "no program";
        if (ViaLauncher(exe)) return $"not measurable (runs through {FileName(exe)})";
        if (r is null) return "not running";
        return $"{ServicePlan.Cost(r.Cores, r.PageFaultsPerSec)} ({r.Processes.ToString(CultureInfo.InvariantCulture)} process{(r.Processes == 1 ? "" : "es")})";
    }

    // ---------------------------------------------------------------- what it costs, on the impact scale

    /// <summary>Under half a core (where a background app counts as heavy) and this many page faults a second: Low.</summary>
    public const double LowBelowCores = 0.5;
    public const long LowBelowFaults = 100_000;

    /// <summary>Under 1.5 cores and this many page faults a second: Medium. At either or more: High.</summary>
    public const double MediumBelowCores = 1.5;
    public const long MediumBelowFaults = 500_000;

    /// <summary>
    /// What a running program costs this PC now, on the scale the Services and the Tasks and startup apps tabs show as a
    /// meter and a word: None while it is idle (under <see cref="ProcessCostHistory.BusyCores"/> of a core and
    /// <see cref="ProcessCostHistory.BusyFaults"/> page faults a second, what <see cref="ServicePlan.Cost"/> calls
    /// "idle"), Low under <see cref="LowBelowCores"/> of a core and <see cref="LowBelowFaults"/> page faults a second,
    /// Medium under <see cref="MediumBelowCores"/> cores and <see cref="MediumBelowFaults"/>, else High. The worse of the
    /// two decides.
    /// </summary>
    public static ImpactLevel Level(double cores, long pageFaultsPerSec)
    {
        if (cores < ProcessCostHistory.BusyCores && pageFaultsPerSec < ProcessCostHistory.BusyFaults) return ImpactLevel.None;
        if (cores < LowBelowCores && pageFaultsPerSec < LowBelowFaults) return ImpactLevel.Low;
        if (cores < MediumBelowCores && pageFaultsPerSec < MediumBelowFaults) return ImpactLevel.Medium;
        return ImpactLevel.High;
    }

    // ---------------------------------------------------------------- who it is from, by where it is installed

    /// <summary>
    /// The folder a program is installed in, as the name of who it is from: "Elgato" for
    /// C:\Program Files\Elgato\StreamDeck\StreamDeck.exe, "Tailscale" for ...\AppData\Local\Tailscale\..., and under
    /// ...\AppData\Local\Programs the app's own folder. Only the path is read, never the file. Null for a program anywhere
    /// else (Windows' own folders, a bare file name, a program right in Program Files) or a folder that names no one
    /// ("Common Files", a Store app's WindowsApps).
    /// </summary>
    public static string? InstallFolderName(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || InstallFolder().Match(exe.Trim()) is not { Success: true } m) return null;
        string first = m.Groups["v"].Value, second = m.Groups["p"].Success ? m.Groups["p"].Value : "";
        string name = first.Equals("Programs", StringComparison.OrdinalIgnoreCase) ? second : first;
        return name.Length == 0 || NotAName.Contains(name) ? null : name;
    }

    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
        { "Common Files", "WindowsApps", "Package Cache", "Temp", "Programs" };

    /// <summary>A program two folders down (vendor, then whatever is under it) in AppData, Program Files or ProgramData.</summary>
    [GeneratedRegex(@"^[A-Za-z]:\\(?:Users\\[^\\]+\\AppData\\(?:Local|Roaming|LocalLow)|Program Files(?: \(x86\))?|ProgramData)\\(?<v>[^\\]+)\\(?:(?<p>[^\\]+)\\)?", RegexOptions.IgnoreCase)]
    private static partial Regex InstallFolder();

    private static string FileName(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}

/// <summary>
/// When a scheduled task starts, in plain words, from the kinds of its triggers as Task Scheduler numbers them
/// (TASK_TRIGGER_TYPE2): "at sign-in", "at startup", "every day", "every day and at sign-in". A kind PoolWatch does not
/// name is left out.
/// </summary>
public static class TaskTriggers
{
    /// <summary>The words for one kind of trigger, or null for a kind PoolWatch does not name.</summary>
    public static string? Word(int type) => type switch
    {
        0 => "when an event is logged",
        1 => "once, at a set time",
        2 => "every day",
        3 => "every week",
        4 or 5 => "every month",
        6 => "when the PC is idle",
        7 => "when it is created or changed",
        8 => "at startup",
        9 => "at sign-in",
        // TASK_TRIGGER_SESSION_STATE_CHANGE: a lock or unlock, or a session connecting or disconnecting (at the PC or remotely).
        11 => "when you lock, unlock or connect to the PC",
        _ => null,
    };

    /// <summary>The words for all of a task's triggers, each once, in the order given; "" when none can be named.</summary>
    public static string Words(IEnumerable<int> types)
    {
        var words = types.Select(Word).OfType<string>().Distinct().ToList();
        return words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
        };
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
                   "turn the service off on the Services tab instead (you can put it back)";
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
