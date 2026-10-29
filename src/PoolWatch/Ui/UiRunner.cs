using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Updates;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Headless;

namespace PoolWatch.Ui;

/// <summary>
/// Runs the tray app. With --ui-smoke N (CI) it opens the window and, after N seconds, draws every window under review
/// to a PNG, checks each (smoke.json), closes them and exits.
/// </summary>
internal static class UiRunner
{
    /// <summary>The longest the CI UI test waits for a window that reads in the background (the guide's scan, the list of changes).</summary>
    private static readonly TimeSpan SmokeWait = TimeSpan.FromSeconds(60);

    public static int Run(CommandLineOptions opt)
    {
        var paths = new AppPaths(opt.OutDir);
        var log = paths.OpenLog();
        var settings = HeadlessRunner.LoadSettings(opt, paths, log);
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
        // The guide for AI assistants in the data folder, written only when missing or older.
        Task.Run(() => AgentsGuide.EnsureInDataRoot(paths, log));
        // The one-time move of the old per-user data, in the background once the tray and the guard run.
        if (!smoke && paths.OutDir is null) paths.StartLegacyMigration(log);
        // An existing logon task that starts PoolWatch from a user-writable folder is shown to the owner,
        // who may point it at this (verified) exe or remove it.
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
    /// CI: every window under review, drawn to a PNG and checked, in the order of the screenshots: the main window
    /// (its Simple and Advanced views, Services, Settings and its Advanced grid), the setup guide once it has read the PC
    /// (it runs no leak check in the test), Active changes once its list is read, Report an issue with a sample text,
    /// About, and the main window's update notice for a sample release. Each window made for the test is closed again, so
    /// at the end only the main window is open.
    /// </summary>
    private static async Task<(List<string> files, object stats)> Smoke(TrayApp app, MonitorEngine engine, AnalysisPipeline analysis, AppPaths paths, string dir)
    {
        var files = app.Form.Screenshots(dir);
        var form = app.Form.SmokeStats(); // hides the main window last
        string Shot(Form window, string name)
        {
            files.Add(SmokeShots.Save(window, dir, name, paths.Guard));
            return name;
        }

        var guide = new SetupGuideForm(app.Setup);
        guide.Show();
        bool guideReady = await Task.WhenAny(guide.Ready, Task.Delay(SmokeWait)) == guide.Ready;
        var guideStats = new { ready = guideReady, stats = guide.SmokeStats(), shot = Shot(guide, "6-setup-guide.png") };
        guide.Close();

        var active = new ActiveChangesForm(app.Setup.Ledger, app.Setup.Actions);
        active.Show();
        bool activeLoaded = await Task.WhenAny(active.Loaded, Task.Delay(SmokeWait)) == active.Loaded;
        var activeStats = new { loaded = activeLoaded, stats = active.SmokeStats(), shot = Shot(active, "7-active-changes.png") };
        active.Close();

        var report = new ReportIssueDialog(engine, analysis, app.Setup.Actions, null);
        report.Show();
        var reportStats = report.SmokeStats(); // types the sample text first, so the shot shows it
        Shot(report, "8-report-issue.png");
        report.Close();

        var about = new AboutDialog(engine, analysis, app.Setup.Actions);
        about.Show();
        var aboutStats = about.SmokeStats();
        Shot(about, "9-about.png");
        about.Close();

        // The update notice as the Simple view shows it when a newer release is out, for a sample release given to the
        // window in the check's place. The sample skips the update-notices setting (CI turns notices off), so it shows
        // whatever the setting. Its text is read after the shot, so the check is of what the image holds.
        app.ShowWindow();
        app.ShowSampleUpdate(new UpdateRelease("1.0.1", ReleasePage.For("v1.0.1")));
        var updateProblems = app.Form.LayoutProblems();
        string updateShot = Shot(app.Form, "10-update-notice.png");
        var updateStats = new { text = app.Form.UpdateNotice, layoutProblems = updateProblems, shot = updateShot };
        app.Form.Hide();

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
            activeChanges = activeStats,
            report = reportStats,
            about = aboutStats,
            updateNotice = updateStats,
            openForms = Application.OpenForms.Cast<Form>().Select(f => f.Text).ToList(),
            trayMenu = app.MenuItemTexts(),
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
            string exe = Environment.ProcessPath ?? "PoolWatch.exe";
            string text = v.Message + "\n\n" + (v.CanRePoint
                ? $"Yes: point it at this PoolWatch ({exe}), installed in an administrator-only folder.\n" +
                  "No: remove the task (tick Start at logon again later if you want it).\n" +
                  "Cancel: leave it for now (PoolWatch asks again at the next start)."
                : "Yes: remove the task now (install PoolWatch to Program Files, then tick Start at logon again).\n" +
                  "No: leave it for now (PoolWatch asks again at the next start).");
            var r = MessageBox.Show(text, "PoolWatch: your logon task is not safe", v.CanRePoint ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            answer = v.CanRePoint
                ? r switch { DialogResult.Yes => LogonTaskCheck.Answer.RePoint, DialogResult.No => LogonTaskCheck.Answer.Remove, _ => LogonTaskCheck.Answer.Leave }
                : r == DialogResult.Yes ? LogonTaskCheck.Answer.Remove : LogonTaskCheck.Answer.Leave;
        }, null);
        return answer;
    }
}
