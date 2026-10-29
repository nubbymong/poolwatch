using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Core.Setup;

/// <summary>Whether the Windows token leak applies to this PC, as far as PoolWatch can tell without running its check.</summary>
public enum TokenLeakStatus { NotAffected, NeedsTest, Leaks, Clean, WorkedAroundAlready, Unknown }

/// <summary>One fixed volume: its root ("D:\"), file system, and for a Dev Drive whether it is trusted (null: not known).</summary>
public sealed record VolumeFact(string Root, string FileSystem, bool IsDevDrive, bool? Trusted);

/// <summary>An AI tool found on this PC ("Claude Code", "ChatGPT desktop", "VS Code") and its version when known.</summary>
public sealed record ToolFact(string Name, string? Version);

/// <summary>
/// What the setup guide knows about this PC: one scan of explicit reads (registry values, fixed paths, the volumes PoolWatch
/// already collects, two WMI values), never a file crawl. Cached in cache\environment.json, which holds no user names and
/// no paths except volume roots.
/// </summary>
public sealed record EnvironmentFacts
{
    /// <summary>A cached file larger than this is not read.</summary>
    public const int MaxJsonChars = 64 * 1024;
    private const int MaxListItems = 32, MaxTextChars = 200;

    public required DateTime ScannedUtc { get; init; }
    /// <summary>"Windows 11 Pro".</summary>
    public required string WindowsName { get; init; }
    /// <summary>"25H2".</summary>
    public string? DisplayVersion { get; init; }
    /// <summary>CurrentBuild.UBR, "26220.7344".</summary>
    public required string Build { get; init; }
    /// <summary>
    /// "Dev", "Beta", "Canary", "Release Preview"; "" when enrolled in the Windows Insider Program on a channel without a name
    /// in words; null when not enrolled.
    /// </summary>
    public string? InsiderChannel { get; init; }
    /// <summary>The processor architecture of Windows itself: "x64" or "ARM64" (an x64 PoolWatch on ARM64 says ARM64).</summary>
    public required string Architecture { get; init; }
    public long RamBytes { get; init; }
    public IReadOnlyList<VolumeFact> Volumes { get; init; } = [];
    public IReadOnlyList<ToolFact> AiTools { get; init; } = [];
    public TokenLeakStatus TokenLeak { get; init; }
    /// <summary>The paths of your own RAMMap clear tasks in Task Scheduler.</summary>
    public IReadOnlyList<string> OwnClearTasks { get; init; } = [];
    public bool LogonTaskExists { get; init; }
    public bool InstalledInProgramFiles { get; init; }
    public int OptionalServicesRunning { get; init; }
    /// <summary>Up to 5 display names of the optional services that are running.</summary>
    public IReadOnlyList<string> OptionalServiceExamples { get; init; } = [];
    public bool? DefenderRealTimeOn { get; init; }
    public bool? DefenderPerformanceModeOn { get; init; }
    public bool ChatGptInstalled { get; init; }
    /// <summary>ChatGPT desktop's exe, for the shortcut's icon only: never shown, never exported, not written to the cache.</summary>
    [JsonIgnore] public string? ChatGptIconPath { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// The cached scan, or null when the text is not one (never throws). The file is PoolWatch's own and admin-only, but it
    /// is still read as untrusted: oversize text is refused, lists and texts are cut to what a scan can produce, and only
    /// volumes a scan can report are kept (a drive root such as "D:\" and a file system name), because the roots end up in
    /// the commands the Defender advice asks the user to paste into PowerShell.
    /// </summary>
    public static EnvironmentFacts? Parse(string json)
    {
        if (json.Length > MaxJsonChars) return null;
        try
        {
            var f = JsonSerializer.Deserialize<EnvironmentFacts>(json, Json);
            if (f is null || string.IsNullOrWhiteSpace(f.WindowsName) || string.IsNullOrWhiteSpace(f.Build) || f.Architecture is not ("x64" or "ARM64")) return null;
            return f with
            {
                WindowsName = Clip(f.WindowsName)!, DisplayVersion = Clip(f.DisplayVersion), Build = Clip(f.Build)!, InsiderChannel = Clip(f.InsiderChannel),
                Volumes = Bound(f.Volumes?.Where(v => v is not null && IsDriveRoot(v.Root) && IsFileSystemName(v.FileSystem))).ToList(),
                AiTools = Bound(f.AiTools?.Where(t => t?.Name is { Length: > 0 })).Select(t => t with { Name = Clip(t.Name)!, Version = Clip(t.Version) }).ToList(),
                OwnClearTasks = Texts(f.OwnClearTasks, MaxListItems),
                OptionalServicesRunning = Math.Max(0, f.OptionalServicesRunning),
                OptionalServiceExamples = Texts(f.OptionalServiceExamples, 5),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    private static string? Clip(string? s) => s is { Length: > MaxTextChars } ? s[..MaxTextChars] : s;

    private static IEnumerable<T> Bound<T>(IEnumerable<T?>? items) where T : class => (items ?? []).OfType<T>().Take(MaxListItems);

    private static List<string> Texts(IEnumerable<string?>? items, int max) => Bound(items?.Where(s => s is { Length: > 0 })).Take(max).Select(s => Clip(s)!).ToList();

    /// <summary>A drive root as Windows lists it, "D:\", and nothing else.</summary>
    public static bool IsDriveRoot(string? root) => root is [var letter, ':', '\\'] && char.IsAsciiLetter(letter);

    /// <summary>A file system's name as Windows reports it: "NTFS", "ReFS", "FAT32".</summary>
    private static bool IsFileSystemName(string? name) => name is { Length: > 0 and <= 16 } && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// The "Your PC" section of the setup guide, one plain line per fact: "Windows 11 Pro 25H2 (Insider, Dev channel), build
    /// 26220.7344", "64-bit (x64)", "64 GB of memory", "Dev Drive: D: (ReFS)", "AI tools: Claude Code, ChatGPT desktop, VS
    /// Code", and why those two matter when either is there.
    /// </summary>
    public IReadOnlyList<string> PlainLines()
    {
        var lines = new List<string>();
        string version = DisplayVersion is { Length: > 0 } d ? $"{WindowsName} {d}" : WindowsName;
        string insider = InsiderChannel switch { null => "", "" => " (Insider)", var channel => $" (Insider, {channel} channel)" };
        lines.Add($"{version}{insider}, build {Build}");
        lines.Add(Architecture == "ARM64"
            ? "ARM64 processor. This PoolWatch is built for x64 and is not tested on ARM yet."
            : "64-bit (x64)");
        if (RamBytes > 0) lines.Add($"{Math.Max(1, Math.Round(RamBytes / (double)(1L << 30)))} GB of memory");
        var devDrives = Volumes.Where(v => v.IsDevDrive).ToList();
        if (devDrives.Count > 0)
            lines.Add($"{(devDrives.Count == 1 ? "Dev Drive" : "Dev Drives")}: {Join(devDrives.Select(v => $"{Letter(v.Root)} ({v.FileSystem})").ToList())}");
        if (AiTools.Count > 0) lines.Add("AI tools: " + string.Join(", ", AiTools.Select(t => t.Name)));
        if (devDrives.Count > 0 || AiTools.Count > 0)
            lines.Add("Why this matters: AI coding tools and Dev Drives work with many thousands of files, and Windows keeps details of " +
                      "recently used files in the kernel pool, which can grow by gigabytes. PoolWatch watches it and can clear it.");
        return lines;
    }

    /// <summary>"D:" for the root "D:\".</summary>
    public static string Letter(string root) => root.TrimEnd('\\');

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}

/// <summary>
/// Whether the token leak applies (the setup guide's token row). The leak is documented on Windows 11 24H2 and 25H2
/// builds, retail as well as Insider (bentoner/windows-token-leak; other builds are untested), so only PoolWatch's check can
/// tell: a saved verdict for this exact build wins, then what the live foreground lock timeout shows.
/// </summary>
public static class TokenLeakRule
{
    /// <summary>Windows 11 24H2's build; the leak is not known on anything older.</summary>
    public const int FirstAffectedBuild = 26100;

    /// <param name="build">CurrentBuild.UBR, "26220.7344".</param>
    /// <param name="saved">The guard's saved verdict (token-guard.json), for whichever build it was.</param>
    /// <param name="liveTimeout">The live foreground lock timeout; 0 means something already works around the leak.</param>
    public static TokenLeakStatus Judge(string build, GuardState? saved, uint? liveTimeout)
    {
        if (Major(build) is not { } major) return TokenLeakStatus.Unknown;
        if (major < FirstAffectedBuild) return TokenLeakStatus.NotAffected;
        if (saved is not null && saved.Build == build)
        {
            if (saved.Result == GuardResult.Leak) return TokenLeakStatus.Leaks;
            if (saved.Result is GuardResult.CleanOnce or GuardResult.Fixed) return TokenLeakStatus.Clean;
        }
        // An unclear or missing verdict for this build: the live value decides whether the check can run at all (it measures
        // the leak with the workaround off).
        return liveTimeout switch
        {
            0 => TokenLeakStatus.WorkedAroundAlready,
            null => TokenLeakStatus.Unknown,
            _ => TokenLeakStatus.NeedsTest,
        };
    }

    /// <summary>The build's major number (26220 for "26220.7344"), or null when it is not one.</summary>
    public static int? Major(string? build) =>
        int.TryParse((build ?? "").Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ? major : null;
}

/// <summary>The Windows Insider channel in words, from the registry's BranchName.</summary>
public static class InsiderChannels
{
    /// <summary>
    /// "Dev", "Beta", "Release Preview", "Canary" for the four channels' branch names (any case); "" for another short plain
    /// name (an older branch name such as "rs_prerelease": enrolled, but with no channel name in words to show); null when
    /// there is none (not enrolled) or it is not a plain name.
    /// </summary>
    public static string? FromBranchName(string? branchName)
    {
        string name = branchName?.Trim() ?? "";
        return name.ToLowerInvariant() switch
        {
            "" => null,
            "dev" => "Dev",
            "beta" => "Beta",
            "releasepreview" => "Release Preview",
            "canarychannel" or "canary" => "Canary",
            _ => name.Length <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? "" : null,
        };
    }
}
