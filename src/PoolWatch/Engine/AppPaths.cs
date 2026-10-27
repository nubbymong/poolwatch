using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Where PoolWatch writes. On Windows everything lives under %ProgramData%\PoolWatch (admin-only: PoolWatch runs
/// elevated, so its data must be out of reach of an unelevated process — S3), except an explicit headless --out
/// directory. On other platforms (the Linux build/CI) it is %LOCALAPPDATA%\PoolWatch. All writes go through
/// <see cref="Guard"/>.
/// </summary>
internal sealed class AppPaths
{
    public string DataRoot { get; }
    public string? OutDir { get; }
    public PathGuard Guard { get; }
    /// <summary>What securing the data root did (moved a foreign one aside, repaired its permissions): for the log and the timeline.</summary>
    public List<string> StartupNotes { get; } = new();

    public AppPaths(string? outDir) : this(DefaultDataRoot(), outDir, secure: OperatingSystem.IsWindows()) { }

    /// <param name="secure">Secure the data root (Windows, the real data root, elevated). Tests pass false and a TEMP folder:
    /// a test never changes an ACL.</param>
    internal AppPaths(string dataRoot, string? outDir, bool secure, IRootOps? rootOps = null)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        // The ReFS rule keeps PoolWatch's own files off the volumes whose pool it measures (the real app, secure: true).
        // Tests (secure: false) write wherever TEMP is, which may be a Dev Drive on a developer's PC.
        Guard = new PathGuard(DataRoot, AppContext.BaseDirectory, refuseVolume: secure ? Collectors.VolumeIdentity.RefusalFor : null,
            isRemote: Collectors.VolumeIdentity.IsRemoteDrive);
        if (outDir is not null)
        {
            OutDir = Path.GetFullPath(outDir);
            Guard.AllowRoot(OutDir);
        }
        if (secure && OperatingSystem.IsWindows() && (rootOps is not null || InstallGuard.IsElevated))
        {
            // S3 (review round 3): the root itself is opened without following links, a foreign or user-writable one is moved
            // aside, a new one is created with its permissions at creation, and they are applied again only when a check
            // through a handle finds them changed (Administrators and SYSTEM full control, read for your account).
            // A link there is moved aside like any untrusted root (M2). Throws DataRootRefusedException only when the root
            // cannot be made safe: nothing is written then.
            StartupNotes.AddRange(DataRootGuard.Prepare(DataRoot, Interop.CurrentUser(), rootOps ?? new WindowsRootOps(), () => DateTime.UtcNow));
        }
        else Directory.CreateDirectory(Guard.CheckReal(DataRoot)); // tests, the Linux build, an unelevated run (it changes nothing elevated)
        if (OutDir is not null) Directory.CreateDirectory(Guard.CheckReal(OutDir));
    }

    /// <summary>
    /// S3: the admin-only data root on Windows (%ProgramData%\PoolWatch). On the Linux build/CI %ProgramData% maps to
    /// a path nothing can write to, so there we keep %LOCALAPPDATA%\PoolWatch — that build never runs elevated.
    /// </summary>
    internal static string DefaultDataRoot() => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        "PoolWatch");

    /// <summary>The old (per-user, user-writable) data root, migrated away from once on Windows (S3).</summary>
    internal static string LegacyDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoolWatch");

    /// <summary>
    /// S3 (review round 2): the one-time move of the old per-user data into the admin-only root, on a background thread
    /// after start-up (it never delays the tray or the logon token guard), link-safe (<see cref="LegacyMigration"/>).
    /// Windows only; a no-op once the marker is there or when there is no old folder.
    /// </summary>
    public void StartLegacyMigration(Log log)
    {
        if (!OperatingSystem.IsWindows()) return;
        string legacy = LegacyDataRoot();
        if (legacy.Equals(DataRoot, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(DataRoot, LegacyMigration.MarkerName))) return;
        new Thread(() =>
        {
            try { log.Info(new LegacyMigration(legacy, DataRoot, Guard.CheckReal, log.Info).Run().Summary()); }
            catch (Exception ex) { log.Info("old data folder not migrated: " + ex.Message); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PoolWatch migration" }.Start();
    }

    /// <summary>
    /// Review round 3: the old per-user settings.json, moved synchronously before the settings are read (link-safe, and
    /// parsed, validated and clamped like any settings load: <see cref="LegacyMigration.MigrateSettings"/>). Windows, the
    /// real data root only. A line for the log, or null.
    /// </summary>
    public string? MigrateLegacySettings()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string legacy = LegacyDataRoot();
        if (legacy.Equals(DataRoot, StringComparison.OrdinalIgnoreCase) || !DataRoot.Equals(Path.GetFullPath(DefaultDataRoot()), StringComparison.OrdinalIgnoreCase)) return null;
        return new LegacyMigration(legacy, DataRoot, Guard.CheckReal, _ => { }).MigrateSettings(json => Core.Settings.Parse(json).ToJson());
    }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>The log, every append and roll-over through the link-resolving write policy (review round 2, S3). What securing
    /// the data root did is its first lines.</summary>
    public Log OpenLog()
    {
        var log = new Log(LogFile, Guard.CheckReal);
        foreach (var n in StartupNotes) log.Info("data folder: " + n);
        return log;
    }

    /// <summary>CSV output: the --out dir, or a daily folder under data\.</summary>
    public string CsvDir(DateTime utcNow) => OutDir ?? Path.Combine(DataRoot, "data", utcNow.ToLocalTime().ToString("yyyyMMdd"));

    public string IncidentsDir => Path.Combine(OutDir ?? DataRoot, "incidents");
    public string ExportsDir => Path.Combine(DataRoot, "exports");
    public string LogFile => Path.Combine(OutDir ?? DataRoot, "poolwatch.log");
    public string CacheDir => Path.Combine(OutDir ?? DataRoot, "cache");
    /// <summary>Boot baselines and per-boot summaries, one folder per boot (kept across reboots, last 60).</summary>
    public string BootsDir => Path.Combine(OutDir ?? DataRoot, "boots");
    /// <summary>Requests from a second PoolWatch.exe (<c>--mark</c>) to the running one.</summary>
    public string RequestsDir => Path.Combine(OutDir ?? DataRoot, "requests");

    /// <summary>Creates a directory after checking the write policy.</summary>
    public string EnsureDir(string dir)
    {
        Directory.CreateDirectory(Guard.CheckReal(dir));
        return dir;
    }

    /// <summary>Deletes daily data folders and incidents older than the retention period (only inside DataRoot).</summary>
    public void ApplyRetention(int days, Action<string> log)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        foreach (var root in new[] { Path.Combine(DataRoot, "data"), Path.Combine(DataRoot, "incidents"), ExportsDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    // S3: CheckReal refuses if this folder (or any folder above it) is a link, so a planted junction
                    // can never turn a retention sweep into a recursive delete of something outside the data root.
                    if (Directory.GetLastWriteTime(dir) < cutoff)
                    {
                        Guard.CheckReal(dir);
                        Directory.Delete(dir, recursive: true);
                        log($"retention: removed {dir}");
                    }
                }
                catch (Exception ex) { log($"retention: could not remove {dir}: {ex.Message}"); }
            }
            foreach (var file in Directory.EnumerateFiles(root))
            {
                try { if (File.GetLastWriteTime(file) < cutoff) { Guard.CheckReal(file); File.Delete(file); } }
                catch { }
            }
        }
    }
}
