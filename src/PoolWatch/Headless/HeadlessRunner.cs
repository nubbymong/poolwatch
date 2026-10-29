using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Engine;

namespace PoolWatch.Headless;

/// <summary>
/// Runs the collectors without UI for a fixed duration (CI) or until stopped. Writes overhead.json and
/// summary.json into the output directory at the end.
/// </summary>
internal static class HeadlessRunner
{
    public static int Run(CommandLineOptions opt)
    {
        var paths = new AppPaths(opt.OutDir);
        var log = paths.OpenLog();
        log.EchoToConsole = true;
        var settings = LoadSettings(opt, paths, log);
        log.Info($"PoolWatch {typeof(HeadlessRunner).Assembly.GetName().Version} headless; out={paths.OutDir ?? paths.DataRoot}; interval={settings.SampleIntervalSeconds}s; duration={opt.DurationSeconds?.ToString() ?? "unbounded"}");

        using var stopRequested = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested.Set(); };
        // The installer and the uninstaller ask through the exit signal: the same clean stop as Ctrl+C.
        using var exitSignal = ExitSignal.Listen(stopRequested.Set, log.Info);
        // A "stop" file in the output folder ends the run cleanly (CI and scripts without a console).
        string stopFile = Path.Combine(paths.OutDir ?? paths.DataRoot, "stop");
        if (File.Exists(stopFile)) File.Delete(paths.Guard.CheckReal(stopFile));

        using var engine = new MonitorEngine(settings, paths, log, opt.VerboseCsv);
        var analysis = new AnalysisPipeline(engine);
        engine.Start();
        if (opt.ExperimentApp is { } app)
            analysis.Diagnostics.StartExperiment(app, TimeSpan.FromMinutes(opt.PhaseMinutes is { } pm && double.IsFinite(pm) ? Math.Clamp(pm, 0.25, 120) : settings.ExperimentPhaseMinutes));
        if (opt.CaptureNowAfter is { } capAfter)
            Task.Run(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(capAfter));
                log.Info(analysis.CaptureNow() is { } ci ? "capture now: " + ci.Id : "capture now: a capture was already running");
            });
        if (opt.ClearCache is { } clear)
        {
            // Once, after the first sample (the clear needs a "before").
            var mode = clear == "lowpriority" ? Core.Cache.ClearMode.LowPriority : Core.Cache.ClearMode.Standby;
            Task.Run(() =>
            {
                for (int i = 0; i < 120 && engine.Latest is null; i++) Thread.Sleep(500);
                log.Info(analysis.Diagnostics.Cache.Request(mode, Core.Cache.ClearTrigger.CommandLine) ? "clear cache requested" : "clear cache not started");
            });
        }
        Task? guard = opt.TokenGuard ? analysis.Diagnostics.StartTokenGuard(null) : null;
        // S3 (review round 2): a headless run on the real data root moves the old per-user data too (in the background).
        if (paths.OutDir is null) paths.StartLegacyMigration(log);
        // S4 (review round 3): an unsafe logon task is reported (no one to ask headless: nothing is changed).
        Task.Run(() =>
        {
            try
            {
                new LogonTaskCheck(new Ui.WindowsLogonTasks(paths), _ => LogonTaskCheck.Answer.Leave, log.Info, analysis.Diagnostics.RecordFromOtherThread)
                    .Run(Environment.ProcessPath, false, InstallGuard.AdminRoots());
            }
            catch (Exception ex) { log.Info("logon task check failed: " + ex.Message); }
        });
        var deadline = opt.DurationSeconds is { } d ? DateTime.UtcNow.AddSeconds(d) : DateTime.MaxValue;
        while (!stopRequested.IsSet && DateTime.UtcNow < deadline && !File.Exists(stopFile))
            stopRequested.Wait(TimeSpan.FromMilliseconds(500));
        bool etwRunning = engine.EtwRunning;
        if (guard is not null && !guard.IsCompleted) log.Info("token guard: still checking at the end of the run");
        analysis.Diagnostics.StopTokenGuard();
        engine.Stop();
        string outRoot = paths.OutDir ?? paths.DataRoot;
        try { analysis.Finish(outRoot); }
        catch (Exception ex) { log.Info("finishing incidents failed: " + ex); }

        string? bundle = null;
        if (opt.ExportBundle)
        {
            try { bundle = BundleBuilder.Export(engine, analysis, opt.NoRedact ? null : Ui.MainForm.CurrentRedaction()); log.Info("vendor bundle: " + bundle); }
            catch (Exception ex) { log.Info("vendor bundle failed: " + ex); }
        }

        var overhead = engine.Meter.Report();
        File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outRoot, "overhead.json")), overhead.ToJson());
        var summary = new
        {
            samples = engine.SampleCount,
            etwRunning,
            etwMode = engine.EtwMode,
            etwEvents = engine.EtwEvents,
            etwEventsLost = engine.EtwEventsLost,
            lastSampleMs = engine.LastSampleDuration.TotalMilliseconds,
            processesTracked = engine.Registry.Snapshot().Count,
            handleScanMs = engine.LatestHandles?.Elapsed.TotalMilliseconds,
            volumes = engine.Volumes,
            poolTagTxt = analysis.Knowledge.PoolTagTxtLoaded,
            alerts = analysis.AlertsRaised,
            capturesStarted = analysis.CapturesStarted,
            capturesSkipped = analysis.CapturesSkipped,
            findings = analysis.Diagnostics.Findings.Snapshot().GroupBy(f => f.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            spawn = new { starts = analysis.Diagnostics.Spawns.StartsSeen, gitStarts = analysis.Diagnostics.Spawns.GitStartsSeen, gitProbes = analysis.Diagnostics.Git.ProbeCount },
            // Identity of every live shared runtime (node, python, pwsh, cmd, git, ...) and WMI-launched process.
            identities = engine.Registry.LiveRecords()
                .Where(r => Core.Identity.ProcessIdentities.SharedRuntimes.Contains(r.Image) || engine.Registry.Ancestry(r.Key).Any(a => a.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(r => { var id = Core.Identity.ProcessIdentities.Of(engine.Registry.Ancestry(r.Key)); return new { pid = r.Pid, image = r.Image, identity = id.Display, role = id.Role, owner = id.Owner.Name, ownerKind = id.Owner.Kind.ToString(), viaWmi = id.ViaWmi, commandLine = r.CommandLine, imagePath = r.ImagePath }; })
                .Take(500).ToList(),
            tokens = analysis.Diagnostics.Tokens.Latest is { } ta ? Core.Tokens.TokenCorrelator.ToData(ta) : null,
            filters = new
            {
                instancesNote = analysis.Diagnostics.Filters.InstancesNote,
                instances = analysis.Diagnostics.Filters.Instances,
                perFilter = analysis.Diagnostics.Filters.Instances?.GroupBy(i => i.Filter).ToDictionary(g => g.Key, g => g.Count()),
                services = analysis.Diagnostics.Filters.Services,
                filterEvents = analysis.Diagnostics.Filters.FilterEvents,
                packageEvents = analysis.Diagnostics.Filters.PackageEvents,
                eventLog = analysis.Diagnostics.EventLog.Status, eventLogDropped = analysis.Diagnostics.EventLog.Dropped,
            },
            system = engine.Latest?.System,
            tagShapes = analysis.Detector.Status.Values.Where(s => s.Rates.Shape != Core.Detection.AllocShape.Idle).OrderByDescending(s => s.Rates.AllocsPerMin).Take(20)
                .Select(s => new { tag = s.Tag.ToString(), allocsPerMin = Math.Round(s.Rates.AllocsPerMin), freesPerMin = Math.Round(s.Rates.FreesPerMin), shape = s.Rates.Shape.ToString() }),
            lag = new
            {
                baseline = analysis.Diagnostics.Lag.Baseline is { } lb ? new { timerMs = Math.Round(lb.timer, 2), messageMs = lb.message, fileMs = Math.Round(lb.file, 2), dwmMissed = lb.dwm } : null,
                latest = analysis.Diagnostics.Lag.Latest is { } ll ? new { ratio = Math.Round(ll.Ratio, 2), worst = ll.Worst, lagging = ll.Lagging, timerMs = Math.Round(ll.Probe.TimerMs, 2) } : null,
                onsetNonPagedBytes = analysis.Diagnostics.Lag.LagOnset?.Pool.CommittedNonPaged,
                dwm = analysis.Diagnostics.DwmAvailable,
                pausedProbes = analysis.Diagnostics.LagProbesPaused,
                summary = analysis.Diagnostics.Lag.Summary(),
            },
            experiment = analysis.Diagnostics.Experiment is { } xp ? new
            {
                app = xp.App, phase = xp.Phase.ToString(), statement = xp.Result?.Statement, notes = xp.Result?.Notes,
                totals = xp.Result?.Totals.Select(t => new { t.Name, t.Baseline, t.WithApp, t.AfterClose, t.GrewWithApp, t.Stopped }),
                grew = xp.Result?.Tags.Where(t => t.GrewWithApp).Take(10).Select(t => new { t.Name, t.Baseline, t.WithApp, t.AfterClose, t.Stopped }),
            } : null,
            accumulation = new
            {
                censuses = analysis.Diagnostics.Accumulation.Censuses,
                top = analysis.Diagnostics.Accumulation.Top(10).Select(g => new { owner = g.OwnerName, what = g.What, count = g.Current, counts = g.Points.Select(p => p.Count) }),
            },
            versions = analysis.Diagnostics.Versions,
            vendorBundle = bundle,
            boots = analysis.Boots.All().Select(b => new { b.BootId, b.UptimeHoursAtLastSeen, b.PeakNonPaged, b.PeakPaged, b.Ended }),
            bootBaseline = analysis.Boots.Baseline is { } bb ? new { bb.BootId, bb.UptimeMinutes, bb.NonPaged, tags = bb.Tags.Count } : null,
            systemCrashes = analysis.Diagnostics.SystemCrashes.Select(c => new { time = c.TimeUtc, bugcheck = c.Code, bucket = c.Bucket, records = c.Records.Count }),
            appCrashes = analysis.Diagnostics.AppCrashes,
            clearExperiment = analysis.Diagnostics.ClearExperiment is { } cx ? new
            {
                phase = cx.Phase.ToString(), statement = cx.Statement, notes = cx.Notes, clearError = cx.ClearError,
                timerMsBefore = cx.Medians(false).timer, timerMsAfter = cx.Medians(true).timer, fileMsBefore = cx.Medians(false).file, fileMsAfter = cx.Medians(true).file,
            } : null,
            marks = analysis.Diagnostics.Findings.Snapshot().Where(f => f.Kind == Core.Findings.FindingKind.Mark).Select(f => f.Title),
            memoryLists = analysis.Diagnostics.Cache.Lists,
            clearHelper = analysis.Diagnostics.Cache.HelperStatus,
            tokenGuard = opt.TokenGuard ? new
            {
                step = analysis.Diagnostics.Guard.Step?.ToString(), checking = analysis.Diagnostics.Guard.Checking,
                liveTimeout = Native.TokenGuardNative.GetForegroundLockTimeout(), wanted = analysis.Diagnostics.Guard.WorkaroundWanted,
                result = analysis.Diagnostics.Guard.State is { } gs ? Core.TokenGuard.GuardState.ResultText(gs.Result) : null,
                build = analysis.Diagnostics.Guard.State?.Build, detail = analysis.Diagnostics.Guard.State?.Detail,
                numbers = analysis.Diagnostics.Guard.TestNumbers, error = analysis.Diagnostics.Guard.TestError, message = analysis.Diagnostics.Guard.LastMessage,
            } : null,
            clears = analysis.Diagnostics.Cache.History().Select(c => new
            {
                time = c.TimeUtc, mode = c.Mode.ToString(), trigger = c.Trigger.ToString(), poolBefore = c.PoolBefore, poolAfter = c.PoolAfter, freed = c.Freed,
                standbyBefore = c.ListsBefore.StandbyBytes, standbyAfter = c.ListsAfter.StandbyBytes, standbyImmediate = c.ListsImmediate.StandbyBytes, immediateKnown = c.ListsImmediate.Known, measured = c.After is not null, error = c.Error, summary = c.Summary(),
            }),
            load = analysis.Diagnostics.Load is { } ld ? new
            {
                cpuFraction = Math.Round(ld.CpuFraction, 3), runQueue = analysis.Diagnostics.RunQueue, systemPageFaultsPerSec = ld.SystemPageFaultsPerSec,
                demandZeroPerSec = analysis.Diagnostics.Faults?.DemandZeroPerSec, transitionPerSec = analysis.Diagnostics.Faults?.TransitionPerSec,
                pagesInputPerSec = analysis.Diagnostics.Faults?.PagesInputPerSec,
                top = ld.Processes.Take(8).Select(r => new { pid = r.Key.Pid, image = r.Image, cores = Math.Round(r.Cores, 2), pageFaultsPerSec = r.PageFaultsPerSec }),
            } : null,
            saturated = analysis.Diagnostics.Saturation.Saturated,
            services = new
            {
                changes = analysis.Diagnostics.Services.Changes.Select(c => new { c.Service, original = c.OriginalStart.ToString(), neu = c.NewStart.ToString(), c.Via }),
                catalogueSize = analysis.Diagnostics.Services.Catalogue.Entries.Count,
            },
            strayGitChecked = analysis.Diagnostics.StrayGit.LastRun.Select(r => new { path = r.Path, where = r.What, found = r.Found }),
            incidents = analysis.Incidents.All.Select(i => new { i.Id, kind = i.Kind.ToString(), tag = i.Tag.ToString(), growth = i.Growth, top = i.Suspects.FirstOrDefault()?.Image }),
        };
        File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outRoot, "summary.json")), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outRoot, "profile.json")), CostReport.Json(engine, analysis));
        log.Info("overhead: " + overhead.Summary());
        log.Info($"done: {engine.SampleCount} samples");
        return 0;
    }

    internal static Settings LoadSettings(CommandLineOptions opt, AppPaths paths, Log? log = null)
    {
        // Review round 3: the old per-user settings.json is moved (checked and clamped) before the first read, so the first
        // session after the move is on the owner's settings, not the defaults.
        if (opt.ConfigFile is null && paths.MigrateLegacySettings() is { } moved) log?.Info(moved);
        // C1 e (review round 2): an explicit --config is read as written; only the default settings.json is migrated.
        var settings = opt.ConfigFile is not null ? Settings.Load(opt.ConfigFile, explicitConfig: true) : Settings.Load(paths.SettingsFile);
        if (opt.IntervalSeconds is { } i) settings.SampleIntervalSeconds = i;
        settings.Normalize();
        return settings;
    }
}
