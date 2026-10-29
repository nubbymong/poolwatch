using PoolWatch.Engine.Changes;
using PoolWatch.Headless;
using PoolWatch.Native;

namespace PoolWatch;

internal static class Program
{
    /// <summary>Exit codes of --stop-running and --uninstall-cleanup that the installer and the uninstaller act on.</summary>
    internal const int ExitStillRunning = 7, ExitNotAllPutBack = 8;
    /// <summary>How long a running PoolWatch is given to exit after it was asked to.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);
    /// <summary>What the uninstall cleanup did, in the data folder (the uninstaller points to it when something was not put back).</summary>
    private const string UninstallSummaryName = "uninstall-summary.txt";

    [STAThread]
    private static int Main(string[] args)
    {
        Core.Util.Profiler.ThreadCpuTicks = NativeMethods.CurrentThreadCpuTicks;
        var opt = CommandLineOptions.Parse(args);
        bool consoleMode = opt.Headless || opt.SelfTest || opt.UiSmokeSeconds is not null || opt.CacheSetup is not null || opt.ServicesDump is not null ||
                           opt.ServiceOff is not null || opt.ServiceRestore is not null || opt.TasksDump is not null || opt.Mark is not null ||
                           opt.StopRunning || opt.UninstallCleanup || opt.ChangesDump is not null || opt.Errors.Count > 0;
        if (consoleMode) NativeMethods.AttachConsole(-1); // ATTACH_PARENT_PROCESS: show output when started from a console
        Engine.RunFlags.AllowAnyLocation = opt.AllowAnyLocation; // S4 (review round 3): never registered as the logon task then
        Engine.RunFlags.UiSmoke = opt.UiSmokeSeconds is not null;
        if (opt.Errors.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, opt.Errors));
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }
        try
        {
            // S4 (review, round 2): an elevated PoolWatch runs only from an administrator-only folder, in every mode that does
            // work: the tray app, headless, the self-test and the services / tasks / mark / clear-cache steps (an unelevated
            // user could swap an exe in a folder they can write to). Exempt: the CI UI smoke (--ui-smoke), the token guard's
            // hidden helper (started by a PoolWatch that passed this check, with its own path), and an explicit
            // --allow-any-location (CI and development: the build output is not in Program Files).
            if (Engine.InstallGuard.Applies(opt) &&
                Engine.InstallGuard.RefuseElevatedFromUserWritable(out string where))
            {
                if (!consoleMode) MessageBox.Show(where, "PoolWatch: install to an administrator-only folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Console.Error.WriteLine(where);
                return 5;
            }
            if (opt.SelfTest) return SelfTest.Run(opt);
            if (opt.Mark is not null) return MarkRequest(opt);
            if (opt.CacheSetup is { } what) return CacheSetup(what, opt);
            // The token-leak guard's hidden helper: before the one-instance mutex (the running PoolWatch started it).
            if (opt.TokenLeakTest is { } tl) return Engine.TokenLeakGuard.RunHelperMode(tl.n, tl.file, new Engine.AppPaths(opt.OutDir), opt.TokenTestParent);
            if (opt.ServicesDump is not null || opt.ServiceOff is not null || opt.ServiceRestore is not null) return Services(opt);
            if (opt.TasksDump is { } td) { var p = new Engine.AppPaths(opt.OutDir); new Engine.TasksAndStartup(p.OpenLog()).Dump(td, p.Guard); Console.WriteLine("tasks written: " + td); return 0; }
            if (opt.ChangesDump is { } cd) return ChangesDump(cd, opt);
            // The installer's and the uninstaller's steps: before the one-instance mutex, which they wait for and take themselves.
            if (opt.StopRunning) return StopRunning();
            if (opt.UninstallCleanup) return UninstallCleanup(opt);
            // One monitor per machine: two would fight over the ETW session and double the overhead.
            using var mutex = new Mutex(true, Engine.ExitSignal.MonitorMutexName, out bool first);
            if (!first)
            {
                if (opt.Headless) { Console.Error.WriteLine("PoolWatch is already running."); return 3; }
                MessageBox.Show("PoolWatch is already running (see the tray).", "PoolWatch");
                return 3;
            }
            if (opt.Headless) return HeadlessRunner.Run(opt);
            return Ui.UiRunner.Run(opt);
        }
        catch (Engine.DataRootRefusedException ex)
        {
            // S3 (review round 3): the data folder could not be made safe (moved aside, created or secured): nothing was written.
            if (!consoleMode) MessageBox.Show(ex.Message, "PoolWatch: data folder not safe to use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Console.Error.WriteLine(ex.Message);
            return 6;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PoolWatch failed: " + ex);
            return 1;
        }
    }

    /// <summary><c>--mark "note"</c>: drops a request for the running instance (it reads it on its next sample).</summary>
    private static int MarkRequest(CommandLineOptions opt)
    {
        string note = Core.Experiments.Marks.Clean(opt.Mark!);
        if (note.Length == 0) { Console.Error.WriteLine("--mark needs a note"); return 2; }
        var paths = new Engine.AppPaths(opt.OutDir);
        string dir = paths.EnsureDir(paths.RequestsDir);
        string path = paths.Guard.CheckReal(Path.Combine(dir, Core.Experiments.Marks.RequestName(DateTime.UtcNow, Environment.ProcessId)));
        File.WriteAllText(paths.Guard.CheckReal(path + ".tmp"), note);
        File.Move(paths.Guard.CheckReal(path + ".tmp"), path);
        Console.WriteLine("Mark queued: " + note);
        return 0;
    }

    /// <summary>
    /// Exit 0: no clear task of your own (any more); 4: a clear task of your own is still there (you kept it, or the note
    /// says why), so the automatic clear must stay off.
    /// </summary>
    private static int Legacy(CommandLineOptions opt)
    {
        // First: a data folder that cannot be made safe stops this before any task is removed.
        var paths = new Engine.AppPaths(opt.OutDir);
        // Each task is recorded with its definition before it is removed, so Active changes and the uninstaller can put it back.
        var (outcome, tasks, note) = Engine.CacheHelper.ResolveLegacy(t => opt.LegacyAnswer ??
            MessageBox.Show(Engine.CacheHelper.LegacyQuestion(t), "PoolWatch: your own clear task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes,
            ChangeRecords.Open(paths, paths.OpenLog()), "the command line");
        Console.WriteLine(outcome switch
        {
            Engine.CacheHelper.LegacyOutcome.Removed => "Removed your clear task (" + Engine.CacheHelper.LegacyNames(tasks) + "); what it ran is left in place.",
            Engine.CacheHelper.LegacyOutcome.Kept when note is not null => $"{note.TrimEnd('.')}. The automatic clear stays off.",
            Engine.CacheHelper.LegacyOutcome.Kept => "Kept your clear task (" + Engine.CacheHelper.LegacyNames(tasks) + "): the automatic clear stays off.",
            _ => "No clear task of your own found.",
        });
        return outcome == Engine.CacheHelper.LegacyOutcome.Kept ? 4 : 0;
    }

    /// <summary><c>--changes-dump &lt;file&gt;</c>: the Active changes list as JSON (CI and support).</summary>
    private static int ChangesDump(string file, CommandLineOptions opt)
    {
        var paths = new Engine.AppPaths(opt.OutDir);
        var changes = ChangeLedger.ForCommandLine(paths, paths.OpenLog()).List();
        File.WriteAllText(paths.Guard.CheckReal(file), ChangeLedger.ToJson(changes));
        Console.WriteLine($"changes written: {file} ({changes.Count})");
        return 0;
    }

    /// <summary><c>--stop-running</c>, run by the installer before it replaces the files. 0: no PoolWatch runs now; 7: one still does.</summary>
    private static int StopRunning()
    {
        if (Engine.ExitSignal.RequestAndWait(StopWait)) return 0;
        Console.Error.WriteLine("PoolWatch is still running.");
        return ExitStillRunning;
    }

    /// <summary>
    /// <c>--uninstall-cleanup [--keep-changes]</c>, run hidden by the uninstaller before it removes any file: stops a running
    /// PoolWatch through its exit signal, puts back what PoolWatch changed and writes uninstall-summary.txt in the data
    /// folder (which the uninstaller keeps unless every change was put back and you chose to delete it). 0: done;
    /// 7: PoolWatch is still running and nothing was changed; 8: something could not be put back.
    /// </summary>
    private static int UninstallCleanup(CommandLineOptions opt)
    {
        var paths = new Engine.AppPaths(opt.OutDir);
        var log = paths.OpenLog();
        // Read once PoolWatch has stopped, so nothing it wrote on its way out is missed.
        var ledger = new Lazy<ChangeLedger>(() => ChangeLedger.ForCommandLine(paths, log));
        return UninstallCleanup(opt.KeepChanges, () => Engine.ExitSignal.StopAndHold(StopWait), () => ledger.Value.List(),
            which => UninstallUndo(ledger.Value, which), summary =>
            {
                Console.Write(summary);
                log.Info("uninstall cleanup: " + summary.TrimEnd().Replace(Environment.NewLine, "; "));
                string file = Path.Combine(paths.DataRoot, UninstallSummaryName);
                File.WriteAllText(paths.Guard.CheckReal(file + ".tmp"), summary);
                File.Move(paths.Guard.CheckReal(file + ".tmp"), paths.Guard.CheckReal(file), overwrite: true);
            });
    }

    /// <summary>
    /// The cleanup with its steps passed in (the tests pass fakes). The one-instance mutex is held from the stop to the
    /// end, so no PoolWatch can start while its changes are being put back.
    /// </summary>
    internal static int UninstallCleanup(bool keepChanges, Func<IDisposable?> stopAndHold, Func<IReadOnlyList<ActiveChange>> list,
        Func<Func<ActiveChange, bool>?, IReadOnlyList<(ActiveChange Change, UndoResult Result)>> undoAll, Action<string> report)
    {
        using var held = stopAndHold();
        if (held is null)
        {
            Console.Error.WriteLine("PoolWatch is still running: nothing was changed.");
            return ExitStillRunning;
        }
        var which = UndoSelection(keepChanges);
        var listed = list();
        var results = undoAll(which);
        report(UninstallSummary(listed, which, results, DateTime.Now));
        return results.All(r => r.Result.Done) ? 0 : ExitNotAllPutBack;
    }

    /// <summary>
    /// The uninstaller's Undo all. Never confirmed: no one is there to accept a warning, so a clear task that runs RAMMap
    /// from a folder you can change is not put back from here (its definition is saved for import, and the run exits 8).
    /// </summary>
    internal static IReadOnlyList<(ActiveChange Change, UndoResult Result)> UninstallUndo(ChangeLedger ledger, Func<ActiveChange, bool>? which) =>
        ledger.UndoAll(which, confirmed: false);

    /// <summary>
    /// What the uninstaller puts back: everything (null), or with --keep-changes only the logon task and the desktop
    /// shortcut, which would otherwise start files that are about to be deleted.
    /// </summary>
    internal static Func<ActiveChange, bool>? UndoSelection(bool keepChanges) =>
        keepChanges ? c => c.Kind is ChangeKind.LogonTask or ChangeKind.Shortcut : null;

    /// <summary>uninstall-summary.txt: each change put back, not put back (and why), or kept, in the uninstaller's words.</summary>
    internal static string UninstallSummary(IReadOnlyList<ActiveChange> listed, Func<ActiveChange, bool>? which,
        IReadOnlyList<(ActiveChange Change, UndoResult Result)> results, DateTime now)
    {
        var text = new System.Text.StringBuilder().AppendLine($"PoolWatch uninstall, {now:yyyy-MM-dd HH:mm}");
        foreach (var (change, result) in results)
            text.AppendLine(result.Done ? "Put back: " + change.What : $"Could not put back: {change.What}. {result.Message}");
        foreach (var change in listed.Where(c => which is not null && !which(c)))
            text.AppendLine("Kept: " + change.What);
        if (results.Count == 0 && listed.Count == 0) text.AppendLine("PoolWatch had made no changes to put back.");
        return text.ToString();
    }

    /// <summary>Background services (addendum 4): dump / turn off / restore from the command line.</summary>
    private static int Services(CommandLineOptions opt)
    {
        try
        {
            var paths = new Engine.AppPaths(opt.OutDir);
            var log = paths.OpenLog();
            // No sampler runs here: process costs are not known (the Cost column and "saved" need the running app).
            var ctl = new Engine.ServicesController(paths, log, f => Console.WriteLine(f.Title), _ => null, _ => null);
            if (opt.ServicesDump is { } file) { ctl.Dump(file); Console.WriteLine("services written: " + file); }
            if (opt.ServiceOff is { } off) foreach (var (s, e) in ctl.Apply([off], "command line")) Console.WriteLine(e is null ? $"{s}: off" : $"{s}: {e}");
            if (opt.ServiceRestore is { } r) foreach (var (s, e) in ctl.Restore(r.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : r)) Console.WriteLine(e is null ? $"{s}: restored" : $"{s}: {e}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("services failed: " + ex.Message); return 1; }
    }

    /// <summary>
    /// The Clear cache steps from an elevated console. S5 (review round 2): there is nothing to set up any more (the purge is
    /// in-process); "remove" takes away what a previous build's setup installed (the two \PoolWatch\ SYSTEM tasks and the
    /// helper files), and the automatic-clear step only resolves a clear task of your own (RAMMap in a scheduled task).
    /// </summary>
    private static int CacheSetup(string what, CommandLineOptions opt)
    {
        try
        {
            switch (what)
            {
                case "remove":
                    var removed = Engine.CacheHelper.RemoveOldTasks();
                    Console.WriteLine(removed.Count == 0 ? "No old Clear cache tasks or helper files found." : "Removed: " + string.Join("; ", removed));
                    return 0;
                case "enable-auto": // turning the automatic clear on: only a clear task of your own (removed on a yes)
                case "remove-legacy": return Legacy(opt);
            }
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Clear cache step failed: " + ex.Message);
            return 1;
        }
    }
}
