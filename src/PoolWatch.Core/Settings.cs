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
    [Description("The time the growth is measured over, in minutes (default 10 for an alert you add; more than 0 and at most 60: a longer window is cut to 60, a rule of 0 is dropped).")]
    public double WindowMinutes { get; set; } = 10;
    [Description("The growth that raises the alert, in bytes, more than 0 (default 209,715,200 for an alert you add, which is 200 MB; 1 GB is 1,073,741,824); a rule of 0 is dropped.")]
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
    [Browsable(false)] // not a choice: every save writes the current version
    [Description("The format version of settings.json. PoolWatch writes it when it saves; it is not a choice.")]
    public int SettingsVersion { get; set; }
    /// <summary>2: the 5-minute handle census. 3: CpuTraceEnabled off by default. 4: RedactCopies on by default.</summary>
    public const int CurrentSettingsVersion = 4;

    // Collection cadence.
    [Description("How often PoolWatch reads the kernel pool, its pool tags and the running processes, in seconds (1 to 300; default 10). A shorter interval costs more.")]
    public double SampleIntervalSeconds { get; set; } = 10;
    /// <summary>The handle census is expensive with a million handles: every 5 minutes, plus on alert and on Capture now.</summary>
    [Description("How often PoolWatch counts the open handles of every process, in seconds (5 to 3,600; default 300), and also when an alert opens or you capture now. A count with a million handles is costly.")]
    public double HandleIntervalSeconds { get; set; } = 300;

    // Clear cache. PoolWatch purges in-process (it runs as administrator); no setup.
    /// <summary>Clear the standby cache automatically (off by default).</summary>
    [Description("Empty the standby cache automatically: on the schedule (AutoClearEveryHours) and at the pool size (AutoClearPoolGb), whichever comes first. Off by default. Applied when you save.")]
    public bool AutoClearEnabled { get; set; }
    /// <summary>Clear when paged + non-paged pool is over this (GB); 0 = no pool rule.</summary>
    [Description("Empty the cache when the kernel pool (paged plus non-paged) reaches this many GB (0 to 1,024; default 8); 0 turns this rule off. A clear at this size that frees little holds the rule back for 6 hours. Applied when you save.")]
    public double AutoClearPoolGb { get; set; } = 8;
    /// <summary>Clear every N hours; 0 = no schedule.</summary>
    [Description("Empty the cache every this many hours, counted from the last clear, or from PoolWatch's start before the first (0 to 168; default 1); 0 turns this rule off. Applied when you save.")]
    public double AutoClearEveryHours { get; set; } = 1;
    /// <summary>Never two automatic clears within this many minutes.</summary>
    [Description("Never two automatic clears within this many minutes (30 to 1,440; default 30). Applied when you save.")]
    public double AutoClearMinMinutes { get; set; } = 30;
    /// <summary>"auto" (low-priority first, the full purge when that frees little), "lowpriority" or "standby".</summary>
    [Description("How an automatic clear empties the cache. auto (default): the light clear (the low-priority part) while it frees about as much as the full one, which it tries once, else the full; lowpriority: only light; standby: only full. Applied when you save.")]
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
    [Description("Sets the foreground lock timeout to 0 against the token leak (pool tag Toke). Turning the guard off puts it back if Windows allows; if not, or if it was already changed, it can stay until you sign out. Off by default.")]
    public bool TokenGuardEnabled { get; set; }
    /// <summary>How often the live foreground lock timeout is re-checked while PoolWatch runs.</summary>
    [Description("While the token-leak guard is on, how often PoolWatch checks that the foreground lock timeout is still 0 and sets it again if something changed it, in minutes (1 to 60; default 3).")]
    public double TokenGuardRecheckMinutes { get; set; } = 3;
    /// <summary>
    /// Update notices, information only: about once a day the tray app asks GitHub for PoolWatch's latest stable release
    /// and says when a newer one is out. On by default (also for an older settings.json without it).
    /// </summary>
    [Description("Tell me when a new version is out. Checks GitHub once a day; information only, nothing is downloaded or installed. On by default. Applied when you save.")]
    public bool CheckForUpdates { get; set; } = true;
    [Description("Length of the CPU trace, when one is recorded (CpuTraceEnabled), in seconds (5 to 120; default 30).")]
    public int CpuTraceSeconds { get; set; } = 30;
    [Description("Local admin-only folders of Windows symbols (PDBs) to name kernel functions in CPU traces, ';'-separated, at most 2,048 characters. Empty (the default) = the 'symbols' folder in the data folder. Never a network drive or a symbol server.")]
    public string SymbolPath { get; set; } = "";
    // How long PoolWatch holds things in memory. The defaults are what the analysis needs; the CI soak shortens them so
    // every window is full within the run and "flat" really means bounded.
    /// <summary>Raw (per-sample) history: 70 min, so the 60-minute rule and an hour of correlation have their data.</summary>
    [Description("How many minutes of samples PoolWatch keeps in memory for its alerts and for working out causes (5 to 300; default 70, so the 60-minute growth alert and an hour of causes have their data).")]
    public double RawHistoryMinutes { get; set; } = 70;
    /// <summary>Exited short-lived processes are forgotten after this (their ancestors of live processes are kept).</summary>
    [Description("How long PoolWatch remembers a process that has exited, in minutes (1 to 600; default 15). The parents of running processes are kept.")]
    public double ShortLivedKeepMinutes { get; set; } = 15;
    /// <summary>Notable events held for incident timelines.</summary>
    [Description("How long notable process events are kept for incident timelines, in minutes (5 to 600; default 90).")]
    public double IncidentEventKeepMinutes { get; set; } = 90;
    [Description("How often PoolWatch reads the services and their states, in seconds (5 to 3,600; default 60), and also when a new service host starts.")]
    public double ServiceIntervalSeconds { get; set; } = 60;
    [Description("How often PoolWatch reads the drives (file system, label, type and Dev Drive flag) into volumes.csv, in minutes (default 60).")]
    public double VolumeIntervalMinutes { get; set; } = 60;
    [Description("A handle count that takes longer than this many milliseconds skips the next one, to keep PoolWatch light (default 250).")]
    public double HandleScanBudgetMs { get; set; } = 250;

    // CSV output.
    [Description("How often the pool tags are written to tags.csv in the data folder, in seconds (default 60; never more often than SampleIntervalSeconds).")]
    public double TagCsvIntervalSeconds { get; set; } = 60;
    [Description("How often the busiest processes are written to processes.csv in the data folder, in seconds (default 60; never more often than SampleIntervalSeconds).")]
    public double ProcessCsvIntervalSeconds { get; set; } = 60;
    [Description("tags.csv leaves out pool tags smaller than this many bytes, apart from the tags PoolWatch always tracks (default 1,048,576, which is 1 MB).")]
    public long TagCsvMinBytes { get; set; } = 1 * MB;
    [Description("How many processes each row of processes.csv lists: those with the most disk reads, writes and other I/O since the last row (default 50).")]
    public int ProcessCsvTopN { get; set; } = 50;

    // Retention.
    [Description("Data, reports and exports are deleted once nothing in them was written for this many days (1 to 90; default 7), checked every 6 hours. Only time the PC is awake counts, never its clock: a clock change never deletes them early.")]
    public int RetentionDays { get; set; } = 7;

    // Detection.
    [Description("The growth alerts: a pool tag that grows by more than Bytes within WindowMinutes opens an incident. Two by default: 200 MB in 10 minutes and 1 GB in 60 minutes.")]
    public List<GrowthRule> GrowthRules { get; set; } = [new(10, 200 * MB), new(60, 1024 * MB)];
    [Description("Alert when the non-paged pool is over this many bytes and still rising (NonPagedTotalRiseBytesPer10Min) (default 5,368,709,120, which is 5 GB).")]
    public long NonPagedTotalAlertBytes { get; set; } = 5L * 1024 * MB;
    /// <summary>"Still rising" for the non-paged total rule: at least this much in 10 minutes.</summary>
    [Description("What counts as still rising for the non-paged pool alert (NonPagedTotalAlertBytes): at least this many bytes in 10 minutes (default 104,857,600, which is 100 MB).")]
    public long NonPagedTotalRiseBytesPer10Min { get; set; } = 100 * MB;
    [Description("A growth incident whose likeliest cause has been quiet, or has exited, for this many minutes while its pool tag still holds at least half of its rise is marked as held (default 15).")]
    public double RetentionMinutes { get; set; } = 15;
    [Description("A pool tag that falls by at least this many bytes from its highest point within ReleaseWindowMinutes is reported as released (default 104,857,600, which is 100 MB).")]
    public long ReleaseBytes { get; set; } = 100 * MB;
    [Description("The window a release (ReleaseBytes) is measured over, in minutes; a tag is reported released at most once per window (default 10).")]
    public double ReleaseWindowMinutes { get; set; } = 10;
    /// <summary>Minimum growth for a tag to be shown as "growing" in the UI table, per 10 minutes.</summary>
    [Description("A pool tag that grows by at least this many bytes in 10 minutes shows as growing in Pool causes (default 10,485,760, which is 10 MB).")]
    public long GrowingDisplayBytesPer10Min { get; set; } = 10 * MB;
    [Description("An incident closes after this many minutes without a new rise (default 10).")]
    public double IncidentQuietMinutes { get; set; } = 10;
    [Description("An incident closes after it has been open this many minutes, whatever it does (default 120).")]
    public double IncidentMaxMinutes { get; set; } = 120;

    // Confirmation trace. Every automatic trace goes through the capture governor (Core.Incidents.CaptureGovernor).
    [Description("Record a confirmation trace when a pool-growth incident opens. At most one automatic capture per AutoCaptureMinMinutes, never while the PC is saturated or in use. Off by default.")]
    public bool CaptureOnAlert { get; set; } = false;
    [Description("Length of the pool-event trace, in seconds (5 to 300; default 60).")]
    public int CaptureSeconds { get; set; } = 60;
    public const int CaptureHardCapSeconds = 300;
    /// <summary>At most one automatic capture (pool alert, lag trip) per this many minutes, counted from the last capture of any kind.</summary>
    [Description("At most one automatic capture per this many minutes, counted from the start and the end of the last capture, Capture now included (1 to 1,440; default 60). Capture now itself is not limited.")]
    public double AutoCaptureMinMinutes { get; set; } = 60;
    /// <summary>Automatic captures also record Kernel-File (pool alerts) and the lag trip's CPU trace (with <see cref="CpuTraceEnabled"/>); off = pool events only.</summary>
    [Description("Automatic captures also record Kernel-File (pool alerts) and, with CpuTraceEnabled, a CPU trace when the lag probe trips. Off by default: pool events only, the lightest trace.")]
    public bool DeeperCapture { get; set; }

    // AICC / Claude Code awareness (optional).
    /// <summary>
    /// An AI Code Conductor resources folder, whose <c>account-profiles\profile-*\.claude</c> folders are searched for Claude
    /// Code transcripts. Empty (the default): the folder is found from the session itself (its CLAUDE_CONFIG_DIR), and
    /// %USERPROFILE%\.claude is always searched.
    /// </summary>
    [Description("An AI Code Conductor resources folder: its account-profiles are searched for Claude Code transcripts. Empty (the default) = found from each session's config folder; %USERPROFILE%\\.claude is always searched. A local drive path only.")]
    public string AiccResourcesRoot { get; set; } = "";
    [Description("A search of Claude Code transcripts, for the session that started a process, reads at most this many files (1 to 5,000; default 400).")]
    public int TranscriptMaxFiles { get; set; } = 400;
    [Description("A search of Claude Code transcripts looks for a process's command within this many minutes before the process started (default 30).")]
    public double TranscriptWindowMinutes { get; set; } = 30;

    // Spawn storms and git roots.
    /// <summary>Window for counting process starts per parent.</summary>
    [Description("The window over which PoolWatch counts the processes each program starts, in seconds (5 to 600; default 30).")]
    public double SpawnStormWindowSeconds { get; set; } = 30;
    /// <summary>A parent that starts more than this many short-lived children within the window opens a spawn storm.</summary>
    [Description("A program that starts more than this many short-lived processes within SpawnStormWindowSeconds is reported as a spawn storm (2 to 100,000; default 30).")]
    public int SpawnStormThreshold { get; set; } = 30;
    /// <summary>A child that exits within this many seconds of its start counts as short-lived.</summary>
    [Description("A process that exits within this many seconds of its start counts as short-lived (0.1 to 600; default 10).")]
    public double ShortLivedSeconds { get; set; } = 10;
    /// <summary>How often to look for a stray .git in the profile, OneDrive and volume roots (also at start).</summary>
    [Description("How often PoolWatch looks for a stray .git in your user folder, the OneDrive folders and the root of each fixed drive, in minutes, and once at start (1 to 1,440; default 60). Only those places are looked at.")]
    public double StrayGitCheckMinutes { get; set; } = 60;

    // Token family vs process starts.
    /// <summary>Bucket length for correlating Toke outstanding allocations with process starts.</summary>
    [Description("The token-leak watch compares the token allocations (the Toke pool tag) with process starts in steps of this many seconds (1 to 3,600; default 60).")]
    public double TokenBucketSeconds { get; set; } = 60;
    /// <summary>How much history the token correlation looks at.</summary>
    [Description("How much history the token-leak watch looks at, in minutes (1 to 1,440; default 60).")]
    public double TokenWindowMinutes { get; set; } = 60;
    /// <summary>Buckets needed before a verdict.</summary>
    [Description("How many steps (TokenBucketSeconds) the token-leak watch needs before it gives a verdict (default 10; at least 4, and never more than the window holds).")]
    public int TokenMinBuckets { get; set; } = 10;
    /// <summary>Toke outstanding allocations must rise at least this much in the window to be called growth.</summary>
    [Description("The token allocations still held must rise by at least this many within the window to count as growth (at least 1; default 500).")]
    public long TokenMinOutstandingRise { get; set; } = 500;

    // Responsiveness probe.
    /// <summary>Seconds between probes (timer, window message, file, DWM). 0 turns the probe off.</summary>
    [Description("Seconds between responsiveness probes (a timer, a window message, a file and the desktop's compositor) (2 to 600; default 10); 0 turns the probe off.")]
    public double LagProbeSeconds { get; set; } = 10;
    /// <summary>Lag = a measure at this many times its baseline (on two probes in a row).</summary>
    [Description("Lag is a probe at this many times its baseline, on two probes in a row (1.5 to 1,000; default 5).")]
    public double LagAlertRatio { get; set; } = 5;
    /// <summary>Probes that make the baseline (the first ones after start: shortly after boot when started at logon).</summary>
    [Description("How many of the first probes after PoolWatch starts make the baseline lag is measured against (3 to 1,000; default 30).")]
    public int LagBaselineSamples { get; set; } = 30;

    /// <summary>Seconds between censuses of live processes per owner and identity (child accumulation).</summary>
    [Description("How often PoolWatch counts the running processes of each program and owner, to find helpers that are started and never exit, in seconds (5 to 3,600; default 300).")]
    public double AccumulationIntervalSeconds { get; set; } = 300;
    /// <summary>Censuses a group must grow over (6 x 5 min = 30 min).</summary>
    [Description("How many of those counts in a row a group must grow over to be reported (2 to 100; default 6, which is 30 minutes at the default interval).")]
    public int AccumulationWindowPoints { get; set; } = 6;
    /// <summary>A flagged group has at least this many processes now...</summary>
    [Description("A group is reported only with at least this many processes now (2 to 10,000; default 5).")]
    public int AccumulationMinCount { get; set; } = 5;
    /// <summary>...and grew by at least this many over the window.</summary>
    [Description("A group is reported only after growing by at least this many processes over those counts (1 to 10,000; default 3).")]
    public int AccumulationMinRise { get; set; } = 3;

    /// <summary>Minutes between app version checks (also once at start).</summary>
    [Description("How often PoolWatch checks the versions of Claude Code, AI Code Conductor and the Store apps it knows, for the timeline, in minutes, and once at start (1 to 1,440; default 60). Crash records are read 5 and 10 minutes in, then this often.")]
    public double VersionCheckMinutes { get; set; } = 60;
    /// <summary>The Claude Code versions folder; empty = %USERPROFILE%\.local\share\claude\versions.</summary>
    [Description("Where Claude Code keeps its versions, for the version check. Empty (the default) = %USERPROFILE%\\.local\\share\\claude\\versions. A local drive path only.")]
    public string ClaudeVersionsDir { get; set; } = "";
    /// <summary>Apps whose crashes (Application Error 1000, WER APPCRASH 1001) are put on the timeline; exe names without .exe.</summary>
    [Description("Programs by name without .exe (at most 100, each up to 64 characters; default 10, ChatGPT and Codex among them): their crashes go on the timeline; a vendor bundle has the pool tags before and after each runs (not runtimes like node).")]
    public List<string> WatchedApps { get; set; } = [.. Versions.CrashMarkers.DefaultWatchedApps];
    /// <summary>On the first run (no record of a previous one), crash records this many days back are read.</summary>
    [Description("At the first start (no record of an earlier one), crash records this many days back are read (0 to 60; default 7).")]
    public int CrashLookbackDays { get; set; } = 7;

    /// <summary>Length of each of the three experiment phases (baseline, app running, app closed).</summary>
    [Description("Minutes per phase of an experiment from the tray menu (1 to 120; default 10): the lag experiment's phases before and after a cache clear; the app experiment's dialog starts at this value and you choose there.")]
    public double ExperimentPhaseMinutes { get; set; } = 10;
    /// <summary>Global hotkey for a timeline mark ("Ctrl+Alt+M"); empty turns it off.</summary>
    [Description("The keys that put a mark on the timeline for a slow moment (at most 40 characters; default Ctrl+Alt+M); empty turns the keys off. The tray menu's Mark always works.")]
    public string MarkHotkey { get; set; } = "Ctrl+Alt+M";

    // Tag knowledge.
    /// <summary>Where the Debugging Tools for Windows install Microsoft's pooltag.txt.</summary>
    public const string DefaultPoolTagTxtPath = @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\triage\pooltag.txt";
    [Description("Microsoft's pooltag.txt (from the Debugging Tools for Windows), read for tag owners and descriptions; default C:\\Program Files (x86)\\Windows Kits\\10\\Debuggers\\x64\\triage\\pooltag.txt. A local drive path only; at most 8 MB is read.")]
    public string? PoolTagTxtPath { get; set; } = DefaultPoolTagTxtPath;

    /// <summary>
    /// Redact the user name, home paths, machine name, SID and common secret patterns in text copied from the window (Copy
    /// row / summary). On by default.
    /// </summary>
    [Description("Redact the user name, home and TEMP paths, machine name, domain, SID and values that look like secrets in text copied from the window. On by default.")]
    public bool RedactCopies { get; set; } = true;

    /// <summary>
    /// The Advanced view's graph shows RAM in use, the cache (standby) and free behind the pool lines: its Show RAM switch,
    /// kept here so it stays as the owner left it. On for a new install (and for a settings.json saved before it existed);
    /// a settings.json that holds it keeps its value.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    public bool ShowRam { get; set; } = true;

    /// <summary>
    /// The owner's named service profiles (each a list of services to keep off), the one applied when PoolWatch starts and
    /// how many minutes after. Not edited in the grid: the Services tab saves them. A profile or entry that cannot be used
    /// is left out when read, with why (<see cref="Services.ServiceProfileSet.Problems"/>, logged at start).
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    public Services.ServiceProfileSet ServiceProfiles { get; set; } = new();

    /// <summary>
    /// How settings.json is read and written. A field PoolWatch does not have is skipped when read (System.Text.Json's
    /// default), and the next save leaves it out: so a settings.json from an earlier build that still holds
    /// <c>startAtLogon</c> (saved, never read: starting at sign-in is the scheduled task Settings and setup create) loads
    /// with every other setting as written.
    /// </summary>
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
    /// Settings from JSON with the same migrations, validation and clamping as <see cref="Load"/>.
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
        // The elevated app reads these paths, so a path must be a plain local drive path ("D:\x"). Anything else (a
        // share, a device or \\?\ path, a relative path, an alternate data stream, a wildcard, a control character, an
        // over-long value) goes back to the default. The symbol path is a ';' list that is checked folder by folder at
        // use (Util.SymbolPaths); here it is only bounded.
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
        // A profile lists only services the shipped catalogue offers and the protected gate allows; the rest is left out.
        ServiceProfiles = Services.ServiceProfileRules.Clean(ServiceProfiles ?? new(), Services.DefaultCatalogue.Shared);
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
