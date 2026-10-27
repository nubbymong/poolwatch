using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Engine;
using PoolWatch.Headless;

namespace PoolWatch.Ui;

/// <summary>Runs the tray app. With --ui-smoke N (CI) it opens the window, renders the window, both tabs and Settings to PNGs after N seconds and exits.</summary>
internal static class UiRunner
{
    public static int Run(CommandLineOptions opt)
    {
        var paths = new AppPaths(opt.OutDir);
        var log = paths.OpenLog();
        var settings = HeadlessRunner.LoadSettings(opt, paths, log);
        log.Info($"PoolWatch {typeof(UiRunner).Assembly.GetName().Version} UI; data={paths.OutDir ?? paths.DataRoot}");
        using var engine = new MonitorEngine(settings, paths, log, verboseCsv: false);
        var analysis = new AnalysisPipeline(engine);
        engine.Start();

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var app = new TrayApp(engine, analysis, showWindow: !opt.Tray || opt.UiSmokeSeconds is not null);
        if (settings.TokenGuardEnabled && opt.UiSmokeSeconds is null)
        {
            // Addendum 3: the token-leak guard's logon check (the tray app is what the logon task starts).
            GuardNotices? notices = null;
            notices = new GuardNotices(SynchronizationContext.Current!, () => analysis.Diagnostics.Guard.TurnOnByOwner(notices!));
            analysis.Diagnostics.StartTokenGuard(notices);
        }
        // S3 (review round 2): the one-time move of the old per-user data, in the background once the tray and the guard run.
        if (opt.UiSmokeSeconds is null && paths.OutDir is null) paths.StartLegacyMigration(log);
        // S4 (review round 3): an existing logon task that starts PoolWatch from a user-writable folder is shown to the owner,
        // who may point it at this (verified) exe or remove it.
        if (opt.UiSmokeSeconds is null)
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
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    string dir = paths.EnsureDir(Path.Combine(paths.OutDir ?? paths.DataRoot, "screenshots"));
                    var files = app.Form.Screenshots(dir);
                    var form = app.Form.SmokeStats();
                    var smoke = new
                    {
                        screenshots = files.Select(Path.GetFileName),
                        form,
                        trayMenu = app.MenuItemTexts(),
                        trayIcon = app.IconState.ToString(),
                        tags = UiData.Tags(analysis, growingOnly: false).Count,
                        processes = engine.Latest?.Processes.Count ?? 0,
                        incidents = UiData.Incidents(analysis, paths.IncidentsDir).Count,
                        state = UiData.State(analysis).ToString(),
                        samples = engine.SampleCount,
                    };
                    File.WriteAllText(paths.Guard.CheckReal(Path.Combine(dir, "smoke.json")), JsonSerializer.Serialize(smoke, new JsonSerializerOptions { WriteIndented = true }));
                    log.Info("ui smoke: " + string.Join(", ", files.Select(Path.GetFileName)));
                }
                catch (Exception ex) { log.Info("ui smoke failed: " + ex); exit = 1; }
                app.ExitThread();
            };
            timer.Start();
            if (secs >= 300) app.Form.StartSoakCycle(); // UI soak: walk the pages like a user so the costly views run
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
                File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outDir, "profile.json")), CostReport.Json(engine, analysis));
                File.WriteAllText(paths.Guard.CheckReal(Path.Combine(outDir, "summary.json")), JsonSerializer.Serialize(new { samples = engine.SampleCount, etwEvents = engine.EtwEvents, processesTracked = engine.Registry.Count }));
            }
            catch (Exception ex) { log.Info("cost files failed: " + ex.Message); }
        }
        return exit;
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
