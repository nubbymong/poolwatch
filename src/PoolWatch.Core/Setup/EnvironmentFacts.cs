using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Core.Setup;

/// <summary>Whether the Windows token leak applies to this PC, as far as PoolWatch can tell without running its check.</summary>
public enum TokenLeakStatus { NotAffected, NeedsTest, Leaks, Clean, WorkedAroundAlready, Unknown }

/// <summary>
/// Whether PoolWatch starts when you sign in, as Settings, the setup guide, the suggestions and Active changes show it:
/// PoolWatch's logon task as Task Scheduler has it, judged as the check at start judges it. Only <see cref="Starts"/> is a
/// task that starts PoolWatch then.
/// </summary>
public enum SignInState
{
    /// <summary>There is no logon task.</summary>
    NoTask,
    /// <summary>The task is on, a sign-in trigger of it is on, the check at start finds it fine, and it runs with highest privileges where it needs them.</summary>
    Starts,
    /// <summary>The task is turned off.</summary>
    TaskOff,
    /// <summary>The task has sign-in triggers, and each is turned off.</summary>
    TriggerOff,
    /// <summary>The task has no sign-in trigger (Task Scheduler still shows it as Ready).</summary>
    NoTrigger,
    /// <summary>
    /// The task runs without highest privileges while UAC gives this account a token without administrator rights for
    /// that: it cannot start PoolWatch's launcher, which starts only as administrator.
    /// </summary>
    NotHighest,
    /// <summary>
    /// Whether it is on, or its sign-in triggers, could not be read, or its run level where that matters (see <see
    /// cref="NotHighest"/>), and nothing that was read keeps it from running.
    /// </summary>
    NotKnown,
    /// <summary>The check at start asks about it: it starts something PoolWatch does not trust, a program that is not PoolWatch's launcher, or a file that is not there.</summary>
    NotSafe,
}

/// <summary>What the <see cref="SignInState"/>s have in common, and the name of the box in Settings that sets the task up.</summary>
public static class SignInStates
{
    /// <summary>Settings' box for the logon task, by the name every text that says to tick it uses.</summary>
    public const string SettingsBox = "Start PoolWatch when I sign in";

    /// <summary>What a text says to do to have the task set up again: "tick Start PoolWatch when I sign in, in Settings".</summary>
    public const string TickInSettings = "tick " + SettingsBox + ", in Settings";

    /// <summary>
    /// The task is there, and a switch of its own keeps it from running when you sign in: it is turned off, its sign-in
    /// triggers are, or it has none. The setup guide and the suggestions leave such a task as it is (turning it on is left
    /// to you, in Settings).
    /// </summary>
    public static bool KeptOff(SignInState state) => state is SignInState.TaskOff or SignInState.TriggerOff or SignInState.NoTrigger;
}

/// <summary>
/// One fixed volume: its root ("D:\"), file system, whether it is a Dev Drive (null: a ReFS volume Windows could not say
/// that of; a volume of another file system is never one, as every Dev Drive is ReFS), and for a Dev Drive whether it is
/// trusted (null: not known).
/// </summary>
public sealed record VolumeFact(string Root, string FileSystem, bool? IsDevDrive, bool? Trusted);

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
    /// <summary>
    /// Whether PoolWatch starts when you sign in, and why not when its logon task is there but does not (<see
    /// cref="SignInState"/>). Required in the cached file, so a cache written before it existed (which said only whether
    /// a task was there, on or off) is not read, and the PC is scanned again.
    /// </summary>
    [JsonRequired] public SignInState SignIn { get; init; }
    public bool InstalledInProgramFiles { get; init; }
    public int OptionalServicesRunning { get; init; }
    /// <summary>Up to 5 display names of the optional services that are running.</summary>
    public IReadOnlyList<string> OptionalServiceExamples { get; init; } = [];
    public bool? DefenderRealTimeOn { get; init; }
    public bool? DefenderPerformanceModeOn { get; init; }
    public bool ChatGptInstalled { get; init; }
    /// <summary>ChatGPT desktop's exe, for the shortcut's icon only: never shown, never exported, not written to the cache.</summary>
    [JsonIgnore] public string? ChatGptIconPath { get; init; }

    // Reads the scan could not make. Each leaves its fact at "none" (no volumes, the Codex app not installed, no service
    // running) without that being known, so setup shows the cause as not known instead. A cache written before these
    // existed reads them as false: read.

    /// <summary>The scan could not read the PC's volumes: <see cref="Volumes"/> is empty without that being known.</summary>
    public bool VolumesNotRead { get; init; }
    /// <summary>
    /// The roots of the volumes the scan could not read whole, none of which is in <see cref="Volumes"/>: a fixed volume,
    /// or one whose drive type Windows could not tell, whose file system Windows could not give (one of a drive type it
    /// could not tell whose file system it gave is in <see cref="Volumes"/>, by that file system). Required in the
    /// cached file, so a cache written before it existed (which left such a volume out without saying so) is not read, and
    /// the PC is scanned again.
    /// </summary>
    [JsonRequired] public IReadOnlyList<string> UnreadVolumes { get; init; } = [];

    /// <summary>
    /// Whether some volume setup looks at could not be read whole: the list (<see cref="VolumesNotRead"/>), a volume's file
    /// system or type (<see cref="UnreadVolumes"/>), or whether a ReFS volume is a Dev Drive. A cause whose level depends on
    /// whether a Dev Drive or a ReFS drive is there is then not known.
    /// </summary>
    [JsonIgnore] public bool VolumesNotAllRead => VolumesNotRead || UnreadVolumes.Count > 0 || Volumes.Any(v => v.IsDevDrive is null);
    /// <summary>The scan could not read the installed Store apps: <see cref="ChatGptInstalled"/> is false without that being known.</summary>
    public bool PackagesNotRead { get; init; }
    /// <summary>The scan could not read the running services: <see cref="OptionalServicesRunning"/> is 0 without that being known.</summary>
    public bool ServicesNotRead { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        // An enum is read only as one of its members' names, as written (MemberNames): a number, a list of names or any
        // other text makes the file unreadable.
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new MemberNames() },
    };

    /// <summary>
    /// Reads an enum only as one of its members' names exactly as written ("Starts"), and writes a member by its name.
    /// JsonStringEnumConverter also reads a list of names ("Starts, TaskOff") as their values combined, which is another
    /// member or none, and a name in another case or with spaces around it. Here each of those, a number, null and any
    /// other text throws a <see cref="JsonException"/>, so every enum value read is one of the enum's members.
    /// </summary>
    private sealed class MemberNames : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Of<>).MakeGenericType(typeToConvert))!;

        private sealed class Of<T> : JsonConverter<T> where T : struct, Enum
        {
            private static readonly Dictionary<string, T> ByName = Enum.GetNames<T>().ToDictionary(n => n, Enum.Parse<T>, StringComparer.Ordinal);

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && ByName.TryGetValue(name, out var value)
                    ? value
                    : throw new JsonException($"Not the name of a {typeof(T).Name}.");

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (!Enum.IsDefined(value)) throw new JsonException($"{value} is not a {typeof(T).Name}.");
                writer.WriteStringValue(value.ToString());
            }
        }
    }

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
            // No enum needs a check of its own here: MemberNames reads each only as one of its members' names.
            if (f is null || string.IsNullOrWhiteSpace(f.WindowsName) || string.IsNullOrWhiteSpace(f.Build) || f.Architecture is not ("x64" or "ARM64")) return null;
            return f with
            {
                WindowsName = Clip(f.WindowsName)!, DisplayVersion = Clip(f.DisplayVersion), Build = Clip(f.Build)!, InsiderChannel = Clip(f.InsiderChannel),
                Volumes = Bound(f.Volumes?.Where(v => v is not null && IsDriveRoot(v.Root) && IsFileSystemName(v.FileSystem))).ToList(),
                UnreadVolumes = Bound(f.UnreadVolumes?.Where(IsDriveRoot)).ToList(),
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
        var devDrives = Volumes.Where(v => v.IsDevDrive == true).ToList();
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
