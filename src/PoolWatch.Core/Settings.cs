using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolWatch.Core;

/// <summary>A growth alert rule: a tag grows by more than <see cref="Bytes"/> within <see cref="WindowMinutes"/>.</summary>
public sealed record GrowthRule
{
    /// <summary>Parameterless for the Settings PropertyGrid's collection editor and for JSON.</summary>
    public GrowthRule() { }
    public GrowthRule(double windowMinutes, long bytes) { WindowMinutes = windowMinutes; Bytes = bytes; }
    public double WindowMinutes { get; set; } = 10;
    public long Bytes { get; set; } = 200L * 1024 * 1024;
    public override string ToString() => $"{Bytes / (1024 * 1024)} MB in {WindowMinutes:0.#} min";
}

/// <summary>
/// User-configurable settings, persisted as JSON in the data root: %ProgramData%\PoolWatch\settings.json on Windows
/// (administrators only), %LOCALAPPDATA%\PoolWatch\settings.json on the Linux build.
/// </summary>
public sealed class Settings
{
    public const long MB = 1024L * 1024;

    /// <summary>Format version of settings.json: older files are migrated on load (e.g. the 60 s handle census default).</summary>
    public int SettingsVersion { get; set; }
    /// <summary>2: the 5-minute handle census. 3: CpuTraceEnabled off by default. 4: RedactCopies on by default.</summary>
    public const int CurrentSettingsVersion = 4;

    // Collection cadence.
    public double SampleIntervalSeconds { get; set; } = 10;
    /// <summary>The handle census is expensive with a million handles: every 5 minutes, plus on alert and on Capture now.</summary>
    public double HandleIntervalSeconds { get; set; } = 300;

    // Clear cache. PoolWatch purges in-process (it runs as administrator); no setup.
    /// <summary>Clear the standby cache automatically (off by default).</summary>
    public bool AutoClearEnabled { get; set; }
    /// <summary>Clear when paged + non-paged pool is over this (GB); 0 = no pool rule.</summary>
    public double AutoClearPoolGb { get; set; } = 8;
    /// <summary>Clear every N hours; 0 = no schedule.</summary>
    public double AutoClearEveryHours { get; set; } = 1;
    /// <summary>Never two automatic clears within this many minutes.</summary>
    public double AutoClearMinMinutes { get; set; } = 30;
    /// <summary>"auto" (low-priority first, the full purge when that frees little), "lowpriority" or "standby".</summary>
    public string AutoClearMode { get; set; } = "auto";
    /// <summary>
    /// Add a CPU trace (sampling with stacks, DPC/ISR, context switches) to Capture now, and (with <see cref="DeeperCapture"/>)
    /// record one when the lag probe trips. Off by default: a 30 s CPU trace with stacks lags a busy PC itself.
    /// </summary>
    [Description("Add a CPU trace (sampling with stacks) to Capture now; with DeeperCapture also when the lag probe trips. Off by default: it lags a busy PC itself.")]
    public bool CpuTraceEnabled { get; set; }
    // Token-leak guard: test for the Toke leak and set the live ForegroundLockTimeout to 0.
    /// <summary>
    /// Run the token-leak guard when the tray app starts, and at once when it is turned on while PoolWatch runs. Off by
    /// default: the leak is documented on Windows 11 24H2 and 25H2, and while the workaround is on any app may take the
    /// focus. The setup guide and a suggestion offer it when this build leaks or the Toke tag grows with process starts.
    /// </summary>
    [Description("Run the token-leak guard when the tray app starts, and at once when turned on: it tests for the Toke leak documented on Windows 11 24H2 and 25H2 and, unless two tests in a row on this build were clean, sets the live foreground lock timeout to 0 until you sign out (any app may then take the focus). Off by default. Applied when you save.")]
    public bool TokenGuardEnabled { get; set; }
    /// <summary>How often the live foreground lock timeout is re-checked while PoolWatch runs.</summary>
    public double TokenGuardRecheckMinutes { get; set; } = 3;
    /// <summary>
    /// Update notices, information only: about once a day the tray app asks GitHub for PoolWatch's latest stable release
    /// and says when a newer one is out. On by default (also for an older settings.json without it).
    /// </summary>
    [Description("Tell me when a new version is out. Checks GitHub once a day; information only, nothing is downloaded or installed.")]
    public bool CheckForUpdates { get; set; } = true;
    [Description("Length of the CPU trace (seconds, 5-120), when one is recorded (CpuTraceEnabled).")]
    public int CpuTraceSeconds { get; set; } = 30;
    [Description("Local admin-only folder(s) with Windows symbols (PDBs), ';'-separated, to name kernel functions in CPU traces. Empty = the 'symbols' folder in PoolWatch's data folder. A folder that users can write to, a network drive or a symbol server is never used (PoolWatch runs as administrator).")]
    public string SymbolPath { get; set; } = "";
    // How long PoolWatch holds things in memory. The defaults are what the analysis needs; the CI soak shortens them so
    // every window is full within the run and "flat" really means bounded.
    /// <summary>Raw (per-sample) history: 70 min, so the 60-minute rule and an hour of correlation have their data.</summary>
    public double RawHistoryMinutes { get; set; } = 70;
    /// <summary>Exited short-lived processes are forgotten after this (their ancestors of live processes are kept).</summary>
    public double ShortLivedKeepMinutes { get; set; } = 15;
    /// <summary>Notable events held for incident timelines.</summary>
    public double IncidentEventKeepMinutes { get; set; } = 90;
    public double ServiceIntervalSeconds { get; set; } = 60;
    public double VolumeIntervalMinutes { get; set; } = 60;
    public double HandleScanBudgetMs { get; set; } = 250;

    // CSV output.
    public double TagCsvIntervalSeconds { get; set; } = 60;
    public double ProcessCsvIntervalSeconds { get; set; } = 60;
    public long TagCsvMinBytes { get; set; } = 1 * MB;
    public int ProcessCsvTopN { get; set; } = 50;

    // Retention.
    public int RetentionDays { get; set; } = 7;

    // Detection.
    public List<GrowthRule> GrowthRules { get; set; } = [new(10, 200 * MB), new(60, 1024 * MB)];
    public long NonPagedTotalAlertBytes { get; set; } = 5L * 1024 * MB;
    /// <summary>"Still rising" for the non-paged total rule: at least this much in 10 minutes.</summary>
    public long NonPagedTotalRiseBytesPer10Min { get; set; } = 100 * MB;
    public double RetentionMinutes { get; set; } = 15;
    public long ReleaseBytes { get; set; } = 100 * MB;
    public double ReleaseWindowMinutes { get; set; } = 10;
    /// <summary>Minimum growth for a tag to be shown as "growing" in the UI table, per 10 minutes.</summary>
    public long GrowingDisplayBytesPer10Min { get; set; } = 10 * MB;
    public double IncidentQuietMinutes { get; set; } = 10;
    public double IncidentMaxMinutes { get; set; } = 120;

    // Confirmation trace. Every automatic trace goes through the capture governor (Core.Incidents.CaptureGovernor).
    [Description("Record a confirmation trace when a pool-growth incident opens. At most one automatic capture per AutoCaptureMinMinutes, never while the PC is saturated or in use.")]
    public bool CaptureOnAlert { get; set; } = false;
    [Description("Length of the pool-event trace (seconds, at most 300).")]
    public int CaptureSeconds { get; set; } = 60;
    public const int CaptureHardCapSeconds = 300;
    /// <summary>At most one automatic capture (pool alert, lag trip) per this many minutes, counted from the last capture of any kind.</summary>
    [Description("At most one automatic capture per this many minutes, counted from the start and the end of the last capture (Capture now included). Capture now itself is not limited.")]
    public double AutoCaptureMinMinutes { get; set; } = 60;
    /// <summary>Automatic captures also record Kernel-File (pool alerts) and the lag trip's CPU trace (with <see cref="CpuTraceEnabled"/>); off = pool events only.</summary>
    [Description("Automatic captures also record Kernel-File (pool alerts) and, with CpuTraceEnabled, a CPU trace when the lag probe trips. Off = pool events only, the lightest trace.")]
    public bool DeeperCapture { get; set; }

    // AICC / Claude Code awareness (optional).
    /// <summary>
    /// An AI Code Conductor resources folder, whose <c>account-profiles\profile-*\.claude</c> folders are searched for Claude
    /// Code transcripts. Empty (the default): the folder is found from the session itself (its CLAUDE_CONFIG_DIR), and
    /// %USERPROFILE%\.claude is always searched.
    /// </summary>
    [Description("Optional: an AI Code Conductor resources folder; its account-profiles are searched for Claude Code transcripts. Empty = found from each session's own config folder; %USERPROFILE%\\.claude is always searched.")]
    public string AiccResourcesRoot { get; set; } = "";
    public int TranscriptMaxFiles { get; set; } = 400;
    public double TranscriptWindowMinutes { get; set; } = 30;

    // Spawn storms and git roots.
    /// <summary>Window for counting process starts per parent.</summary>
    public double SpawnStormWindowSeconds { get; set; } = 30;
    /// <summary>A parent that starts more than this many short-lived children within the window opens a spawn storm.</summary>
    public int SpawnStormThreshold { get; set; } = 30;
    /// <summary>A child that exits within this many seconds of its start counts as short-lived.</summary>
    public double ShortLivedSeconds { get; set; } = 10;
    /// <summary>How often to look for a stray .git in the profile, OneDrive and volume roots (also at start).</summary>
    public double StrayGitCheckMinutes { get; set; } = 60;

    // Token family vs process starts.
    /// <summary>Bucket length for correlating Toke outstanding allocations with process starts.</summary>
    public double TokenBucketSeconds { get; set; } = 60;
    /// <summary>How much history the token correlation looks at.</summary>
    public double TokenWindowMinutes { get; set; } = 60;
    /// <summary>Buckets needed before a verdict.</summary>
    public int TokenMinBuckets { get; set; } = 10;
    /// <summary>Toke outstanding allocations must rise at least this much in the window to be called growth.</summary>
    public long TokenMinOutstandingRise { get; set; } = 500;

    // Responsiveness probe.
    /// <summary>Seconds between probes (timer, window message, file, DWM). 0 turns the probe off.</summary>
    public double LagProbeSeconds { get; set; } = 10;
    /// <summary>Lag = a measure at this many times its baseline (on two probes in a row).</summary>
    public double LagAlertRatio { get; set; } = 5;
    /// <summary>Probes that make the baseline (the first ones after start: shortly after boot when started at logon).</summary>
    public int LagBaselineSamples { get; set; } = 30;

    /// <summary>Seconds between censuses of live processes per owner and identity (child accumulation).</summary>
    public double AccumulationIntervalSeconds { get; set; } = 300;
    /// <summary>Censuses a group must grow over (6 x 5 min = 30 min).</summary>
    public int AccumulationWindowPoints { get; set; } = 6;
    /// <summary>A flagged group has at least this many processes now...</summary>
    public int AccumulationMinCount { get; set; } = 5;
    /// <summary>...and grew by at least this many over the window.</summary>
    public int AccumulationMinRise { get; set; } = 3;

    /// <summary>Minutes between app version checks (also once at start).</summary>
    public double VersionCheckMinutes { get; set; } = 60;
    /// <summary>The Claude Code versions folder; empty = %USERPROFILE%\.local\share\claude\versions.</summary>
    public string ClaudeVersionsDir { get; set; } = "";
    /// <summary>Apps whose crashes (Application Error 1000, WER APPCRASH 1001) are put on the timeline; exe names without .exe.</summary>
    public List<string> WatchedApps { get; set; } = [.. Versions.CrashMarkers.DefaultWatchedApps];
    /// <summary>On the first run (no record of a previous one), crash records this many days back are read.</summary>
    public int CrashLookbackDays { get; set; } = 7;

    /// <summary>Length of each of the three experiment phases (baseline, app running, app closed).</summary>
    public double ExperimentPhaseMinutes { get; set; } = 10;
    /// <summary>Global hotkey for a timeline mark ("Ctrl+Alt+M"); empty turns it off.</summary>
    public string MarkHotkey { get; set; } = "Ctrl+Alt+M";

    // Tag knowledge.
    /// <summary>Where the Debugging Tools for Windows install Microsoft's pooltag.txt.</summary>
    public const string DefaultPoolTagTxtPath = @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\triage\pooltag.txt";
    [Description("Microsoft's pooltag.txt (from the Debugging Tools for Windows), read for tag owners and descriptions. A local drive path only; at most 8 MB is read.")]
    public string? PoolTagTxtPath { get; set; } = DefaultPoolTagTxtPath;

    /// <summary>
    /// Redact the user name, home paths, machine name, SID and common secret patterns in text copied from the window (Copy
    /// row / summary). On by default.
    /// </summary>
    [Description("Redact the user name, home and TEMP paths, machine name, domain, SID and values that look like secrets in text copied from the window. On by default.")]
    public bool RedactCopies { get; set; } = true;

    /// <summary>Not edited in the grid: the "Start at logon" checkbox creates / removes the scheduled task itself.</summary>
    [System.ComponentModel.Browsable(false)]
    public bool StartAtLogon { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="explicitConfig">
    /// True for a file given with <c>--config</c>. The version migrations below exist because
    /// PoolWatch's own Save wrote every setting, old defaults included, into the default settings.json, so an old value
    /// there is not a choice. A hand-written --config file without <c>settingsVersion</c> holds only what its author
    /// chose (a CI config asking for <c>cpuTraceEnabled: true</c>), so it is read exactly as written, never migrated.
    /// </param>
    public static Settings Load(string path, bool explicitConfig = false) =>
        File.Exists(path) ? Parse(File.ReadAllText(path), explicitConfig) : new Settings();

    /// <summary>
    /// Settings from JSON with the same migrations, validation and clamping as <see cref="Load"/> (the old
    /// per-user settings.json, a user-writable file, goes through this before the elevated app takes it over).
    /// </summary>
    public static Settings Parse(string json, bool explicitConfig = false)
    {
        var s = JsonSerializer.Deserialize<Settings>(json, Json) ?? new Settings();
        if (!explicitConfig)
        {
            if (s.SettingsVersion < 2 && s.HandleIntervalSeconds == 60) s.HandleIntervalSeconds = 300; // v1.0 default, not a choice
            // Every saved file before v3 says cpuTraceEnabled: true (the old default, written out by Save), so it is not a
            // choice either: the CPU trace with stacks lags a busy PC. Turned on again after this, it is kept.
            if (s.SettingsVersion < 3) s.CpuTraceEnabled = false;
            // Every saved file before v4 says redactCopies: false (the old default, written out by Save): not a choice.
            if (s.SettingsVersion < 4) s.RedactCopies = true;
        }
        s.SettingsVersion = CurrentSettingsVersion;
        s.Normalize();
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SettingsVersion = CurrentSettingsVersion;
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Clamps values to safe ranges (the overhead budget depends on these).</summary>
    public void Normalize()
    {
        SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, 1, 300);
        HandleIntervalSeconds = Math.Clamp(HandleIntervalSeconds, 5, 3600);
        AutoClearPoolGb = double.IsFinite(AutoClearPoolGb) ? Math.Clamp(AutoClearPoolGb, 0, 1024) : 8;
        AutoClearEveryHours = double.IsFinite(AutoClearEveryHours) ? Math.Clamp(AutoClearEveryHours, 0, 24 * 7) : 1;
        AutoClearMinMinutes = double.IsFinite(AutoClearMinMinutes) ? Math.Clamp(AutoClearMinMinutes, 30, 24 * 60) : 30; // at most once per 30 min
        AutoClearMode = AutoClearMode is "lowpriority" or "standby" ? AutoClearMode : "auto";
        CpuTraceSeconds = Math.Clamp(CpuTraceSeconds, 5, 120);
        TokenGuardRecheckMinutes = double.IsFinite(TokenGuardRecheckMinutes) ? Math.Clamp(TokenGuardRecheckMinutes, 1, 60) : 3;
        RawHistoryMinutes = double.IsFinite(RawHistoryMinutes) ? Math.Clamp(RawHistoryMinutes, 5, 300) : 70;
        ShortLivedKeepMinutes = double.IsFinite(ShortLivedKeepMinutes) ? Math.Clamp(ShortLivedKeepMinutes, 1, 600) : 15;
        IncidentEventKeepMinutes = double.IsFinite(IncidentEventKeepMinutes) ? Math.Clamp(IncidentEventKeepMinutes, 5, 600) : 90;
        ServiceIntervalSeconds = Math.Clamp(ServiceIntervalSeconds, 5, 3600);
        TagCsvIntervalSeconds = Math.Max(TagCsvIntervalSeconds, SampleIntervalSeconds);
        ProcessCsvIntervalSeconds = Math.Max(ProcessCsvIntervalSeconds, SampleIntervalSeconds);
        RetentionDays = Math.Clamp(RetentionDays, 1, 90);
        CaptureSeconds = Math.Clamp(CaptureSeconds, 5, CaptureHardCapSeconds);
        // Never back to back; 1 minute is the floor (CI configs), the default an hour.
        AutoCaptureMinMinutes = double.IsFinite(AutoCaptureMinMinutes) ? Math.Clamp(AutoCaptureMinMinutes, 1, 24 * 60) : 60;
        TranscriptMaxFiles = Math.Clamp(TranscriptMaxFiles, 1, 5000);
        SpawnStormWindowSeconds = Math.Clamp(SpawnStormWindowSeconds, 5, 600);
        SpawnStormThreshold = Math.Clamp(SpawnStormThreshold, 2, 100_000);
        ShortLivedSeconds = Math.Clamp(ShortLivedSeconds, 0.1, 600);
        StrayGitCheckMinutes = Math.Clamp(StrayGitCheckMinutes, 1, 24 * 60);
        TokenBucketSeconds = Math.Clamp(TokenBucketSeconds, 1, 3600);
        AccumulationIntervalSeconds = double.IsFinite(AccumulationIntervalSeconds) ? Math.Clamp(AccumulationIntervalSeconds, 5, 3600) : 300;
        AccumulationWindowPoints = Math.Clamp(AccumulationWindowPoints, 2, 100);
        AccumulationMinCount = Math.Clamp(AccumulationMinCount, 2, 10_000);
        AccumulationMinRise = Math.Clamp(AccumulationMinRise, 1, 10_000);
        VersionCheckMinutes = double.IsFinite(VersionCheckMinutes) ? Math.Clamp(VersionCheckMinutes, 1, 24 * 60) : 60;
        // The elevated app reads these paths, and an old per-user settings.json (a file the user could write) is migrated
        // through here, so a path must be a plain local drive path ("D:\x"). Anything else (a share, a device or \\?\ path,
        // a relative path, an alternate data stream, a wildcard, a control character, an over-long value) goes back to the
        // default. The symbol path is a ';' list that is checked folder by folder at use (Util.SymbolPaths); here it is
        // only bounded.
        AiccResourcesRoot = LocalPathOr(AiccResourcesRoot, "");
        ClaudeVersionsDir = LocalPathOr(ClaudeVersionsDir, "");
        PoolTagTxtPath = string.IsNullOrEmpty(PoolTagTxtPath) ? PoolTagTxtPath : LocalPathOr(PoolTagTxtPath, DefaultPoolTagTxtPath);
        SymbolPath = SymbolPath is { Length: <= MaxSymbolPathLength } sp && !sp.Any(char.IsControl) ? sp : "";
        WatchedApps = (WatchedApps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
            .Where(a => a.Length <= 64 && a.IndexOfAny(NotInAppName) < 0 && !a.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        CrashLookbackDays = Math.Clamp(CrashLookbackDays, 0, 60);
        ExperimentPhaseMinutes = double.IsFinite(ExperimentPhaseMinutes) ? Math.Clamp(ExperimentPhaseMinutes, 1, 120) : 10;
        MarkHotkey = MarkHotkey is { Length: <= 40 } hk ? hk : "";
        LagProbeSeconds = LagProbeSeconds <= 0 ? 0 : Math.Clamp(LagProbeSeconds, 2, 600);
        LagAlertRatio = Math.Clamp(LagAlertRatio, 1.5, 1000);
        LagBaselineSamples = Math.Clamp(LagBaselineSamples, 3, 1000);
        TokenWindowMinutes = Math.Clamp(TokenWindowMinutes, 1, 24 * 60);
        // A verdict needs TokenMinBuckets in the window: never ask for more buckets than the window can hold.
        TokenMinBuckets = Math.Clamp(TokenMinBuckets, 4, Math.Max(4, (int)(TokenWindowMinutes * 60 / TokenBucketSeconds) - 1));
        TokenMinOutstandingRise = Math.Max(1, TokenMinOutstandingRise);
        GrowthRules ??= [];
        // Rules edited in the UI or a hand-written settings.json: drop nonsense, keep windows within the raw history.
        GrowthRules = GrowthRules.Where(r => r is not null && r.WindowMinutes > 0 && r.Bytes > 0)
            .Select(r => new GrowthRule(Math.Min(r.WindowMinutes, 60), r.Bytes)).ToList();
    }

    public const int MaxPathSettingLength = 260, MaxSymbolPathLength = 2048;
    private static readonly char[] NotInPath = ['*', '?', '"', '<', '>', '|'];
    private static readonly char[] NotInAppName = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// True for a plain local drive path: a drive letter, a colon and a separator ("C:\x", "D:/x"), at most
    /// <see cref="MaxPathSettingLength"/> characters, no further colon (an alternate data stream), no wildcard or quote and
    /// no control character. UNC, device (\\.\, \\?\), relative and drive-relative ("C:x") paths are not.
    /// </summary>
    public static bool IsLocalDrivePath(string? p) =>
        p is { Length: >= 3 and <= MaxPathSettingLength } && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] is '\\' or '/') &&
        p.IndexOf(':', 2) < 0 && p.IndexOfAny(NotInPath) < 0 && !p.Any(char.IsControl);

    /// <summary>The value, trimmed, when it is empty or a plain local drive path; else <paramref name="fallback"/>.</summary>
    private static string LocalPathOr(string? value, string fallback)
    {
        string v = value?.Trim() ?? "";
        return v.Length == 0 || IsLocalDrivePath(v) ? v : fallback;
    }
}
