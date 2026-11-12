using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Updates;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Headless;
using PoolWatch.Ui.Setup;

namespace PoolWatch.Ui;

/// <summary>
/// Runs the tray app. With --ui-smoke N (CI) it opens the window and, after N seconds, draws every window under review
/// to a PNG, checks each (smoke.json), closes them and exits.
/// </summary>
internal static class UiRunner
{
    /// <summary>The longest the CI UI test waits for a window that reads in the background (setup's scan, the list of changes).</summary>
    private static readonly TimeSpan SmokeWait = TimeSpan.FromSeconds(60);

    /// <param name="hold">The proof that this thread holds the one-instance mutex (Program.Main runs the tray app only
    /// through <see cref="ExitSignal.AsTheOneInstance"/>): its start may move data folders.</param>
    public static int Run(CommandLineOptions opt, ExitSignal.Hold hold)
    {
        var paths = new AppPaths(opt.OutDir, hold);
        var log = paths.OpenLog();
        var settings = HeadlessRunner.LoadSettings(opt, paths);
        log.Info($"PoolWatch {typeof(UiRunner).Assembly.GetName().Version} UI; data={paths.OutDir ?? paths.DataRoot}");
        HeadlessRunner.LogKnowledgeProblems(log);
        using var engine = new MonitorEngine(settings, paths, log, verboseCsv: false);
        var analysis = new AnalysisPipeline(engine);
        engine.Start();
        bool smoke = opt.UiSmokeSeconds is not null;

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        // Settings that apply at once (the automatic clear, the token-leak guard, update notices) also go to settings.json,
        // except with --config (read-only); one list of every change PoolWatch made, with Undo.
        var live = new LiveSettings(paths, settings, persist: opt.ConfigFile is null, log);
        var ledger = ChangeLedger.ForApp(engine, analysis, live);
        var app = new TrayApp(engine, analysis, live, ledger, showWindow: !opt.Tray || smoke, smoke);
        // Every hour, PoolWatch's own cost and what it holds (cache\profile.json), the main window's rankings included.
        engine.Profile = () => CostReport.Json(engine, analysis, app.RankingsHeld());
        // The token-leak guard's sign-in check (the tray app is what the logon task starts).
        if (settings.TokenGuardEnabled && !smoke) _ = app.StartTokenGuard();
        // The service profile chosen to apply at start, after its delay, through Apply's own path; it asks nothing, and the
        // tray says what it changed (a click opens Active changes) and where the log says why a service was not changed.
        using var stopProfile = new CancellationTokenSource();
        analysis.Diagnostics.Services.Profiles = () => live.Running.ServiceProfiles;
        _ = ProfileAtStart.ForApp(live, analysis.Diagnostics.Services, log.Info, smoke, explicitConfig: opt.ConfigFile is not null, CacheHelper.IsElevated,
            stopProfile.Token, done: (profile, run) => { if (ProfileAtStart.Notice(profile, run, log.CurrentPath) is { } text) app.NotifyChanges(text); });
        // The guide for AI assistants in the data folder, written only when missing or older.
        Task.Run(() => AgentsGuide.EnsureInDataRoot(paths, log));
        // An existing logon task that is not exactly the one PoolWatch makes (it starts PoolWatch from a user-writable
        // folder, a program with PoolWatch's name that is not its launcher, or anything PoolWatch cannot check; or it differs
        // from PoolWatch's own in any other part; or it cannot be read) is shown to the owner, who may replace it with
        // PoolWatch's own task for this PoolWatch's (verified) launcher, or remove it.
        if (!smoke)
        {
            var ui = SynchronizationContext.Current!;
            Task.Run(() =>
            {
                try
                {
                    new LogonTaskCheck(new WindowsLogonTasks(paths), v => AskAboutLogonTask(ui, v), log.Info, analysis.Diagnostics.RecordFromOtherThread)
                        .Run(Environment.ProcessPath, LogonTaskPolicy.WhyNotRegister(Environment.ProcessPath, opt.AllowAnyLocation, false, InstallGuard.AdminRoots()) is null,
                            InstallGuard.AdminRoots());
                }
                catch (Exception ex) { log.Info("logon task check failed: " + ex.Message); }
            });
        }
        int exit = 0;
        if (opt.UiSmokeSeconds is { } secs)
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)(Math.Max(1, secs) * 1000) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    string dir = paths.EnsureDir(Path.Combine(paths.OutDir ?? paths.DataRoot, "screenshots"));
                    var (files, smokeStats) = await Smoke(app, engine, analysis, paths, dir);
                    File.WriteAllText(paths.Guard.CheckReal(Path.Combine(dir, "smoke.json")), JsonSerializer.Serialize(smokeStats, new JsonSerializerOptions { WriteIndented = true }));
                    log.Info("ui smoke: " + string.Join(", ", files.Select(Path.GetFileName)));
                }
                catch (Exception ex) { log.Info("ui smoke failed: " + ex); exit = 1; }
                app.ExitThread();
            };
            timer.Start();
            if (secs >= 300) app.Form.StartSoakCycle(); // UI soak: walk the views like a user so the costly ones run
        }
        Application.Run(app);
        stopProfile.Cancel();
        analysis.Diagnostics.StopTokenGuard();
        engine.Stop();
        try { analysis.Finish(paths.OutDir ?? paths.DataRoot); } catch (Exception ex) { log.Info("finishing incidents failed: " + ex); }
        if (paths.OutDir is { } outDir)
        {
            // CI (UI soak / smoke): the same cost files the headless run writes.
            try
            {
                File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outDir, "overhead.json")), engine.Meter.Report().ToJson());
                File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outDir, "profile.json")), CostReport.Json(engine, analysis, app.RankingsHeld()));
                File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outDir, "summary.json")), JsonSerializer.Serialize(new { samples = engine.SampleCount, etwEvents = engine.EtwEvents, processesTracked = engine.Registry.Count }));
            }
            catch (Exception ex) { log.Info("cost files failed: " + ex.Message); }
        }
        return exit;
    }

    /// <summary>
    /// CI: every window under review, drawn to a PNG and checked: About and Report an issue with a sample text first, each
    /// of which reads PoolWatch's own cost as it opens, so that they read it as the run left it; then the main window (its
    /// Simple and Advanced views, Services, Tasks and startup apps, Settings and its Advanced grid), each step of setup once
    /// it has read the PC (it runs no leak check in the test) and "How PoolWatch works", Active changes once its list is
    /// read, and the main window's update notice for a sample release. Drawn after the others, About read the working set
    /// that the 8 windows (16 screenshots) made in about 4 s had added, with no trim between them: 127 to 131 MB at the end of the UI soak,
    /// where the steady peak was 86 to 96 MB, held against the 100 MB budget. Each window made for the test is closed
    /// again, so at the end only the main window is open.
    /// </summary>
    private static async Task<(List<string> files, object stats)> Smoke(TrayApp app, MonitorEngine engine, AnalysisPipeline analysis, AppPaths paths, string dir)
    {
        app.Form.StopSoakCycle(); // the UI soak's walk: its last step is due with this timer's tick
        // PoolWatch's own cost as About judges it, read before this walk adds its own (the UI soak checks it).
        var meter = engine.Meter.Report();
        var meterBeforeWalk = new
        {
            judged = meter.Judged, judgedFromSeconds = meter.JudgedFromSeconds, judgedSeconds = meter.JudgedSeconds,
            judgedCpuPercentOfOneCore = meter.JudgedCpuPercentOfOneCore, judgedPeakWorkingSetBytes = meter.JudgedPeakWorkingSetBytes,
            judgedDiskWriteBytesPerHour = meter.JudgedDiskWriteBytesPerHour,
        };
        var files = new List<string>();
        string Shot(Form window, string name)
        {
            files.Add(SmokeShots.Save(window, dir, name, paths.Guard));
            return name;
        }

        // About, then Report an issue, before any other window is built (the UI soak checks what About says).
        var about = new AboutDialog(engine, analysis, app.Setup.Actions);
        about.Show();
        var aboutStats = about.SmokeStats();
        Shot(about, "9-about.png");
        about.Close();

        var report = new ReportIssueDialog(engine, analysis, app.Setup.Actions, null);
        report.Show();
        var reportStats = report.SmokeStats(); // types the sample text first, so the shot shows it
        Shot(report, "8-report-issue.png");
        report.Close();

        files.AddRange(app.Form.Screenshots(dir));
        var form = app.Form.SmokeStats(); // hides the main window last

        // Setup as on a first run (its close button asks first), each of its eight steps once it has read the PC, then
        // "How PoolWatch works", then its Records step alone, as it opens by itself after an upgrade from 1.0.0-beta.2.
        // Nothing is applied: the test goes straight to each step, and every change refuses in the test anyway.
        var guide = new SetupWizardForm(app.Setup, firstRun: true, advanced: false);
        guide.Show();
        bool guideReady = await Task.WhenAny(guide.Ready, Task.Delay(SmokeWait)) == guide.Ready;
        var steps = new List<object>();
        foreach (var step in SetupFlow.Steps)
        {
            await guide.ShowStepForTest(step);
            var stats = guide.SmokeStats();
            steps.Add(new { stats, shot = Shot(guide, SetupWizardForm.ShotName(step)) });
        }
        var how = guide.OpenHowItWorks();
        Application.DoEvents(); // it lays out as it loads
        var howStats = new { stats = how.SmokeStats(), shot = Shot(how, SetupWizardForm.HowItWorksShot) };
        how.Close();
        var guideStats = new { ready = guideReady, window = guide.WindowStats(), steps, howItWorks = howStats };
        guide.CloseForTest();

        var records = new SetupWizardForm(app.Setup, firstRun: false, advanced: false, recordsOnly: true);
        records.Show();
        bool recordsReady = await Task.WhenAny(records.Ready, Task.Delay(SmokeWait)) == records.Ready;
        var recordsStats = new { ready = recordsReady, window = records.WindowStats(), stats = records.SmokeStats(), shot = Shot(records, SetupWizardForm.RecordsAloneShot) };
        records.CloseForTest();

        var active = new ActiveChangesForm(app.Setup.Ledger, app.Setup.Actions);
        active.Show();
        bool activeLoaded = await Task.WhenAny(active.Loaded, Task.Delay(SmokeWait)) == active.Loaded;
        var activeStats = new { loaded = activeLoaded, stats = active.SmokeStats(), shot = Shot(active, "7-active-changes.png") };
        active.Close();

        // The update notice as the Simple view shows it when a newer release is out, for a sample release given to the
        // window in the check's place. The sample skips the update-notices setting (CI turns notices off), so it shows
        // whatever the setting. Its text is read after the shot, so the check is of what the image holds.
        app.ShowWindow();
        app.ShowSampleUpdate(new UpdateRelease("1.0.1", ReleasePage.For("v1.0.1")));
        var updateProblems = app.Form.LayoutProblems();
        string updateShot = Shot(app.Form, "10-update-notice.png");
        var updateStats = new { text = app.Form.UpdateNotice, layoutProblems = updateProblems, shot = updateShot };
        app.Form.Hide();

        // The tray menu's Settings... clicked as a user would, the main window hidden in the Simple view (whose header has no
        // Settings button): Settings opens on its own, centred on the screen, with a taskbar button; it is closed again.
        var traySettings = app.SettingsFromTrayForTest();

        // The tray icon's colour and what decides it (UiData.State), under one lock so no sample comes between them (the
        // lock is re-entrant, and UiData.State takes it too).
        int openIncidents, retainedTags;
        lock (analysis.History.SyncRoot)
        {
            app.UpdateIcon();
            openIncidents = analysis.Incidents.Open.Count();
            retainedTags = analysis.Detector.Status.Values.Count(s => s.State == TagState.Retained);
        }
        return (files, new
        {
            screenshots = files.Select(Path.GetFileName),
            form,
            guide = guideStats,
            recordsAlone = recordsStats,
            activeChanges = activeStats,
            report = reportStats,
            about = aboutStats,
            meterBeforeWalk,
            updateNotice = updateStats,
            openForms = Application.OpenForms.Cast<Form>().Select(f => f.Text).ToList(),
            trayMenu = app.MenuItemTexts(),
            traySettings,
            trayIcon = app.IconState.ToString(),
            openIncidents,
            retainedTags,
            // Every setting's own name, which the CI test looks for in each text a user reads (none may show there).
            settingNames = typeof(Core.Settings).GetProperties().Select(p => p.Name),
            tags = UiData.Tags(analysis, growingOnly: false).Count,
            processes = engine.Latest?.Processes.Count ?? 0,
            incidents = UiData.Incidents(analysis, paths.IncidentsDir).Count,
            state = UiData.State(analysis).ToString(),
            samples = engine.SampleCount,
        });
    }

    /// <summary>The owner's choice for an unsafe logon task (asked on the UI thread; the check waits for it).</summary>
    private static LogonTaskCheck.Answer AskAboutLogonTask(SynchronizationContext ui, LogonTaskPolicy.Verdict v)
    {
        var answer = LogonTaskCheck.Answer.Leave;
        ui.Send(_ =>
        {
            // Re-pointing makes the task start this PoolWatch's launcher (LogonTask.TaskXml).
            string start = Environment.ProcessPath is { } exe ? LaunchGate.LauncherOf(exe, LayoutFiles.OnDisk) ?? LaunchGate.ExpectedLauncher(exe) : LaunchGate.LauncherFileNames[0];
            string text = v.Message + "\n\n" + (v.CanRePoint
                ? $"Yes: replace it with PoolWatch's own task, for this PoolWatch ({start}), installed in an administrator-only folder{LogonTaskCheck.RePointKeeps(v.On, v.SignInTrigger)}.\n" +
                  $"No: remove the task (to have it again later, {Core.Setup.SignInStates.TickInSettings}).\n" +
                  "Cancel: leave it for now (PoolWatch asks again at the next start)."
                : $"Yes: remove the task now (install PoolWatch to Program Files, then {Core.Setup.SignInStates.TickInSettings}).\n" +
                  "No: leave it for now (PoolWatch asks again at the next start).");
            var r = MessageBox.Show(text, "PoolWatch: your logon task is not safe", v.CanRePoint ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            answer = v.CanRePoint
                ? r switch { DialogResult.Yes => LogonTaskCheck.Answer.RePoint, DialogResult.No => LogonTaskCheck.Answer.Remove, _ => LogonTaskCheck.Answer.Leave }
                : r == DialogResult.Yes ? LogonTaskCheck.Answer.Remove : LogonTaskCheck.Answer.Leave;
        }, null);
        return answer;
    }
}
