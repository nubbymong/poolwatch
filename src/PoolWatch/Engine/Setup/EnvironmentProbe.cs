using System.Runtime.InteropServices;
using Microsoft.Win32;
using PoolWatch.Analysis;
using PoolWatch.Collectors;
using PoolWatch.Core.Model;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Core.Versions;
using PoolWatch.Native;

namespace PoolWatch.Engine.Setup;

/// <summary>
/// The setup guide's look at this PC (<see cref="EnvironmentFacts"/>): run at the first start, on demand, and when the main
/// window opens with facts older than a day or from another Windows build. Never on a timer. One scan at a time, on a
/// worker thread; the result is kept in memory and in cache\environment.json.
/// <para>
/// PoolWatch runs as the account that elevated it. When a standard user elevates with another administrator's password, the
/// per-user facts (the AI tools in the profile, the Store packages) are that administrator's.
/// </para>
/// </summary>
internal sealed class EnvironmentProbe
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    private const string FileName = "environment.json";

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly IEnvironmentReads _reads;
    private readonly object _lock = new();
    private EnvironmentFacts? _last;
    private bool _cacheRead;
    private Task<EnvironmentFacts>? _scan;

    public EnvironmentProbe(MonitorEngine engine, AnalysisPipeline analysis, IEnvironmentReads? reads = null)
        : this(engine.Paths, engine.Log, reads ?? new WindowsEnvironmentReads(engine, analysis)) { }

    internal EnvironmentProbe(AppPaths paths, Log log, IEnvironmentReads reads)
    {
        _paths = paths;
        _log = log;
        _reads = reads;
    }

    private string CacheFile => _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.CacheDir), FileName));

    /// <summary>The last scan: in memory, else cache\environment.json (read once); null before the first.</summary>
    public EnvironmentFacts? Last
    {
        get
        {
            lock (_lock)
            {
                if (_last is null && !_cacheRead)
                {
                    _cacheRead = true;
                    _last = ReadCache();
                }
                return _last;
            }
        }
    }

    /// <summary>Scans on a worker thread; while a scan runs, every caller gets that one.</summary>
    public Task<EnvironmentFacts> ScanAsync()
    {
        lock (_lock)
        {
            if (_scan is { IsCompleted: false } running) return running;
            return _scan = Task.Run(() =>
            {
                var facts = Scan();
                lock (_lock) _last = facts;
                WriteCache(facts);
                return facts;
            });
        }
    }

    /// <summary>
    /// The facts are missing, older than 24 hours, dated after now (the clock was set back, or the file was edited: they
    /// would otherwise never age), or from another Windows build (one registry read).
    /// </summary>
    public bool Stale(DateTime nowUtc) => Last is not { } f || f.ScannedUtc > nowUtc || nowUtc - f.ScannedUtc > StaleAfter || f.Build != Build();

    private string Build()
    {
        string build = Read(() => _reads.CurrentVersion("CurrentBuild"), null) ?? "";
        return Read(() => _reads.CurrentVersion("UBR"), null) is { } ubr ? $"{build}.{ubr}" : build;
    }

    private EnvironmentFacts Scan()
    {
        string build = Build();
        // ProductName still says "Windows 10" on Windows 11 (it was never changed): build 22000 and later is Windows 11.
        string name = Read(() => _reads.CurrentVersion("ProductName"), null) is { Length: > 0 } product ? product : "Windows";
        if (TokenLeakRule.Major(build) >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name[10..];
        var volumes = Read(_reads.Volumes, []).Where(v => v.DriveType == "Fixed" && v.FileSystem.Length > 0)
            .Select(v => new VolumeFact(v.Root, v.FileSystem, v.IsDevDrive == true, v.IsDevDrive == true ? Read(() => _reads.DevDriveTrusted(v.Root), null) : null))
            .ToList();
        string? chatGpt = ChatGptPackage(Read(_reads.PackageFullNames, []));
        var optional = Read(_reads.RunningOptionalServices, []);
        var (realTime, performanceMode) = Read(_reads.Defender, (null, null));
        var facts = new EnvironmentFacts
        {
            ScannedUtc = DateTime.UtcNow, WindowsName = name, DisplayVersion = Read(() => _reads.CurrentVersion("DisplayVersion"), null), Build = build,
            InsiderChannel = InsiderChannels.FromBranchName(Read(_reads.InsiderBranchName, null)),
            Architecture = Read(_reads.NativeMachine, null) == ImageFileMachineArm64 ? "ARM64" : "x64",
            RamBytes = Read(_reads.RamBytes, 0), Volumes = volumes, AiTools = AiTools(chatGpt is not null),
            TokenLeak = TokenLeakRule.Judge(build, Read(_reads.SavedGuardState, null), Read(_reads.LiveForegroundLockTimeout, null)),
            OwnClearTasks = Read(_reads.OwnClearTasks, []), LogonTaskExists = Read(_reads.LogonTaskExists, false),
            InstalledInProgramFiles = Read(_reads.InstalledInProgramFiles, false),
            OptionalServicesRunning = optional.Count, OptionalServiceExamples = optional.Take(5).ToList(),
            DefenderRealTimeOn = realTime, DefenderPerformanceModeOn = performanceMode,
            ChatGptInstalled = chatGpt is not null, ChatGptIconPath = chatGpt is null ? null : IconPath(chatGpt),
        };
        _log.Info($"setup: scanned this PC: {facts.WindowsName} {facts.DisplayVersion} build {build}, {facts.Architecture}, " +
                  $"{volumes.Count(v => v.IsDevDrive)} Dev Drive(s), {facts.AiTools.Count} AI tool(s), token leak {facts.TokenLeak}, " +
                  $"{facts.OwnClearTasks.Count} own clear task(s), {optional.Count} optional service(s) running");
        return facts;
    }

    /// <summary>IMAGE_FILE_MACHINE_ARM64.</summary>
    internal const ushort ImageFileMachineArm64 = 0xAA64;

    /// <summary>The ChatGPT desktop package (the newest staged version, by its full name), or null.</summary>
    internal static string? ChatGptPackage(IEnumerable<string> fullNames) => fullNames
        .Where(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("OpenAI.ChatGPT", StringComparison.OrdinalIgnoreCase))
        .Select(n => (name: n, version: VersionMarkers.FromPackageFullName(n)?.version))
        .OrderByDescending(p => Version.TryParse(p.version, out var v) ? v : new Version())
        .Select(p => p.name).FirstOrDefault();

    /// <summary>The package's ChatGPT.exe, when it is there: the shortcut's icon.</summary>
    private string? IconPath(string package) =>
        Read(() => _reads.PackageRootFolder(package), null) is { } root && Path.Combine(root, "app", "ChatGPT.exe") is var exe
        && Read(() => _reads.PathExists(exe), false) ? exe : null;

    /// <summary>The AI tools: the versions PoolWatch tracks (Store packages, Claude Code, AI Code Conductor) and a few fixed paths.</summary>
    private List<ToolFact> AiTools(bool chatGptDesktop)
    {
        var tools = new List<ToolFact>();
        var versions = Read(_reads.Versions, new Dictionary<string, string>());
        foreach (var (key, name) in new[] { ("Claude Code", "Claude Code"), ("ChatGPT", "ChatGPT desktop"), ("Codex", "Codex desktop"), ("Claude", "Claude desktop"), ("AI Code Conductor", "AI Code Conductor") })
            if (versions.TryGetValue(key, out var v)) tools.Add(new ToolFact(name, v));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Add(string name, params string[] paths)
        {
            if (tools.All(t => t.Name != name) && paths.Any(p => Read(() => _reads.PathExists(p), false))) tools.Add(new ToolFact(name, null));
        }
        Add("VS Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe"));
        Add("Cursor", Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
        // The ChatGPT desktop app keeps its Codex data in .codex too: only without it does the folder mean the Codex CLI.
        if (!chatGptDesktop) Add("Codex CLI", Path.Combine(profile, ".codex"));
        Add("Claude Code", Path.Combine(profile, ".claude"));
        return tools;
    }

    /// <summary>A read that fails counts as not known, and the scan goes on (logged with the read's code).</summary>
    private T Read<T>(Func<T> read, T fallback, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(read))] string what = "")
    {
        try { return read(); }
        catch (Exception ex)
        {
            _log.Info($"setup: {what} failed: {ex.Message}");
            return fallback;
        }
    }

    private EnvironmentFacts? ReadCache()
    {
        try
        {
            string file = CacheFile;
            if (!File.Exists(file) || new FileInfo(file).Length > EnvironmentFacts.MaxJsonChars) return null;
            return EnvironmentFacts.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) { _log.Info("setup: environment.json not read: " + ex.Message); return null; }
    }

    private void WriteCache(EnvironmentFacts facts)
    {
        try
        {
            string file = CacheFile;
            File.WriteAllText(_paths.Guard.CheckReal(file + ".tmp"), facts.ToJson());
            File.Move(_paths.Guard.CheckReal(file + ".tmp"), _paths.Guard.CheckReal(file), overwrite: true);
        }
        catch (Exception ex) { _log.Info("setup: environment.json not saved: " + ex.Message); }
    }
}

/// <summary>Every read a scan makes, so a test can stand in for Windows. Each is one explicit read; none searches.</summary>
internal interface IEnvironmentReads
{
    /// <summary>A value under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion as text (ProductName, DisplayVersion, CurrentBuild, UBR).</summary>
    string? CurrentVersion(string name);
    /// <summary>HKLM\SOFTWARE\Microsoft\WindowsSelfHost\Applicability\BranchName; null when not enrolled.</summary>
    string? InsiderBranchName();
    /// <summary>Windows' own processor architecture (IMAGE_FILE_MACHINE_*), from IsWow64Process2; null when not known.</summary>
    ushort? NativeMachine();
    long RamBytes();
    IReadOnlyList<VolumeInfo> Volumes();
    bool? DevDriveTrusted(string root);
    /// <summary>The app versions PoolWatch tracks, by name ("Claude Code", "ChatGPT", "Codex", "Claude", "AI Code Conductor").</summary>
    IReadOnlyDictionary<string, string> Versions();
    /// <summary>The per-user AppModel package repository's package full names.</summary>
    IReadOnlyList<string> PackageFullNames();
    string? PackageRootFolder(string fullName);
    bool PathExists(string path);
    GuardState? SavedGuardState();
    uint? LiveForegroundLockTimeout();
    /// <summary>Your own RAMMap clear tasks' paths (Task Scheduler, elevated).</summary>
    IReadOnlyList<string> OwnClearTasks();
    bool LogonTaskExists();
    bool InstalledInProgramFiles();
    /// <summary>Display names of the running services on the optional-services catalogue.</summary>
    IReadOnlyList<string> RunningOptionalServices();
    /// <summary>Microsoft Defender's real-time protection and Dev Drive performance mode; null where not known.</summary>
    (bool? RealTime, bool? PerformanceMode) Defender();
}

/// <summary>The reads on Windows, from the registry, the volumes and services the engine already collects, and WMI.</summary>
internal sealed class WindowsEnvironmentReads(MonitorEngine engine, AnalysisPipeline analysis) : IEnvironmentReads
{
    /// <summary>The same key <see cref="VersionCollector"/> reads the Store package versions from.</summary>
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public string? CurrentVersion(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return k?.GetValue(name) switch { string s => s, int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
    }

    public string? InsiderBranchName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
        return k?.GetValue("BranchName") as string;
    }

    public ushort? NativeMachine() => NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(), out _, out ushort native) ? native : null;

    public long RamBytes() => analysis.Host.GetSystemInfo().RamBytes;

    /// <summary>The engine's volumes (refreshed hourly), or a query of its own before its first sample.</summary>
    public IReadOnlyList<VolumeInfo> Volumes() => engine.Volumes.Count > 0 ? engine.Volumes : VolumeCollector.Query();

    public bool? DevDriveTrusted(string root) => VolumeCollector.QueryDevDriveTrusted(root);

    /// <summary>The versions of the last check, or a read of its own before the first (a few seconds after start).</summary>
    public IReadOnlyDictionary<string, string> Versions() =>
        analysis.Diagnostics.Versions is { Count: > 0 } v ? v : VersionCollector.Read(engine.Settings, null, engine.Log.Info);

    public IReadOnlyList<string> PackageFullNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
        return key?.GetSubKeyNames() ?? [];
    }

    public string? PackageRootFolder(string fullName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PackagesKey + "\\" + fullName);
        return key?.GetValue("PackageRootFolder") as string;
    }

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public GuardState? SavedGuardState() => analysis.Diagnostics.Guard.ReadState();

    public uint? LiveForegroundLockTimeout() => TokenGuardNative.GetForegroundLockTimeout();

    public IReadOnlyList<string> OwnClearTasks() => CacheHelper.IsElevated ? CacheHelper.FindLegacy().Select(t => t.Path).ToList() : [];

    public bool LogonTaskExists() => Ui.LogonTask.Read() is not null;

    public bool InstalledInProgramFiles() => Environment.ProcessPath is { } exe && InstallGuard.IsTrustedInstallPath(exe);

    /// <summary>From the engine's running services, or a query of its own before the engine's first services refresh.</summary>
    public IReadOnlyList<string> RunningOptionalServices() => (engine.Services.Count > 0 ? engine.Services : ServiceCollector.Query())
        .Where(s => analysis.Diagnostics.Services.Catalogue.Find(s.Name) is not null)
        .Select(s => s.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// MSFT_MpComputerStatus.RealTimeProtectionEnabled and MSFT_MpPreference.PerformanceModeStatus (0 is on). Never the
    /// exclusion list. Unknown (another antivirus, Defender off or too old) is null.
    /// </summary>
    public (bool? RealTime, bool? PerformanceMode) Defender() =>
        (Wmi("RealTimeProtectionEnabled", "MSFT_MpComputerStatus") is bool rt ? rt : null,
         Wmi("PerformanceModeStatus", "MSFT_MpPreference") is { } pm && IsNumber(pm) ? Convert.ToInt32(pm, System.Globalization.CultureInfo.InvariantCulture) == 0 : null);

    private static bool IsNumber(object o) => o is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static object? Wmi(string property, string wmiClass)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", $"SELECT {property} FROM {wmiClass}",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true });
            using var results = searcher.Get();
            foreach (var o in results)
                using (o) return o[property];
            return null;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or COMException or UnauthorizedAccessException) { return null; }
    }
}
