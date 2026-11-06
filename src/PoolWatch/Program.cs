using PoolWatch.Engine.Changes;
using PoolWatch.Headless;
using PoolWatch.Native;

namespace PoolWatch;

internal static class Program
{
    /// <summary>How long a running PoolWatch is given to exit after it was asked to.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);
    /// <summary>What the uninstall cleanup did, in the data folder (the uninstaller points to it when something was not put back).</summary>
    internal const string UninstallSummaryName = "uninstall-summary.txt";
    /// <summary>
    /// A cleanup's summary that could not be written to <see cref="UninstallSummaryName"/>, kept for the next cleanup to
    /// write there (<see cref="WriteSummary"/>).
    /// </summary>
    internal const string KeptSummaryName = "uninstall-summary-kept.txt";

    [STAThread]
    private static int Main(string[] args)
    {
        Core.Util.Profiler.ThreadCpuTicks = NativeMethods.CurrentThreadCpuTicks;
        var opt = CommandLineOptions.Parse(args);
        bool consoleMode = opt.Headless || opt.SelfTest || opt.UiSmokeSeconds is not null || opt.CacheSetup is not null || opt.ServicesDump is not null ||
                           opt.ServiceOff is not null || opt.ServiceRestore is not null || opt.TasksDump is not null || opt.Mark is not null ||
                           opt.StopRunning || opt.UninstallCleanup || opt.ChangesDump is not null || opt.Errors.Count > 0;
        if (consoleMode) NativeMethods.AttachConsole(-1); // ATTACH_PARENT_PROCESS: show output when started from a console
        Engine.RunFlags.AllowAnyLocation = opt.AllowAnyLocation; // Never registered as the logon task then
        Engine.RunFlags.UiSmoke = opt.UiSmokeSeconds is not null;
        if (opt.Errors.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, opt.Errors));
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return Engine.ExitCodes.BadCommandLine;
        }
        // An elevated PoolWatch runs only with the environment its launcher (the install folder's PoolWatch.exe) built: any
        // other start is handed to the launcher, or refused (Engine.LaunchGate). Before anything else that does work.
        if (Engine.LaunchGate.Check(args, exempt: !Engine.InstallGuard.Applies(opt), wait: consoleMode) is { } handedOver) return handedOver;
        // The files named on the command line, as the caller meant them (against the folder the launcher was started in,
        // which starts PoolWatch in its own app folder); then PoolWatch's own folder (administrator-only once installed) is
        // its current one, also in a start without the launcher, so nothing it opens or loads by a bare name comes from
        // the folder it was started in.
        opt.ResolvePaths(Engine.LaunchGate.StartedIn(Environment.GetEnvironmentVariables(), Environment.CurrentDirectory));
        UseOwnFolder();
        var tell = TellingFor(opt, consoleMode);
        try
        {
            // An elevated PoolWatch runs only from an administrator-only folder, in every mode that does
            // work: the tray app, headless, the self-test and the services / tasks / mark / clear-cache steps (an unelevated
            // user could swap an exe in a folder they can write to). Exempt: the CI UI smoke (--ui-smoke), the token guard's
            // hidden helper (started by a PoolWatch that passed this check, with its own path), and an explicit
            // --allow-any-location (CI and development: the build output is not in Program Files).
            if (Engine.InstallGuard.Applies(opt) &&
                Engine.InstallGuard.RefuseElevatedFromUserWritable(out string where))
            {
                if (!consoleMode) MessageBox.Show(where, "PoolWatch: install to an administrator-only folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Console.Error.WriteLine(where);
                return Engine.ExitCodes.NotInAdminFolder;
            }
            if (opt.SelfTest) return SelfTest.Run(opt);
            if (opt.Mark is not null) return MarkRequest(opt);
            if (opt.CacheSetup is { } what) return CacheSetup(what, opt);
            // The token-leak guard's hidden helper: before the one-instance mutex (the running PoolWatch started it). Like every
            // step here that does not hold that mutex, it is given no proof of a hold on it, so it moves no data folder (AppPaths).
            if (opt.TokenLeakTest is { } tl) return Engine.TokenLeakGuard.RunHelperMode(tl.n, tl.file, new Engine.AppPaths(opt.OutDir), opt.TokenTestParent);
            if (opt.ServicesDump is not null || opt.ServiceOff is not null || opt.ServiceRestore is not null) return Services(opt);
            if (opt.TasksDump is { } td) { var p = new Engine.AppPaths(opt.OutDir); new Engine.TasksAndStartup(p.OpenLog()).Dump(td, p.Guard); Console.WriteLine("tasks written: " + td); return Engine.ExitCodes.Done; }
            if (opt.ChangesDump is { } cd) return ChangesDump(cd, opt);
            // The installer's and the uninstaller's steps: before the one-instance mutex, which they wait for and take themselves.
            if (opt.StopRunning) return StopRunning(opt);
            if (opt.UninstallCleanup) return UninstallCleanup(opt);
            // One monitor per machine: two would fight over the ETW session and double the overhead. The mutex is in
            // PoolWatch's private namespace, which only an elevated administrator or SYSTEM can create or open; a failure
            // to take it is said, never taken for "already running". The tray app and a headless run each take the proof
            // that this thread holds that mutex, and make their paths with it.
            return RunAsTheOnlyMonitor(MonitorMutexName,
                hold => opt.Headless ? HeadlessRunner.Run(opt, hold) : Ui.UiRunner.Run(opt, hold),
                () =>
                {
                    if (opt.Headless) Console.Error.WriteLine("PoolWatch is already running.");
                    else MessageBox.Show("PoolWatch is already running (see the tray).", "PoolWatch");
                },
                message => TellNotStarted(tell, message));
        }
        catch (Engine.DataRootRefusedException ex)
        {
            // PoolWatch could not use its data folder (it could not be made safe, or the data in a folder
            // of its own could not be moved back to it) and no folder of PoolWatch's own could be made in its place either
            // (AppPaths: one is used otherwise); or the usual folder, or the folder of its own that PoolWatch's data is in,
            // cannot be read now, or cannot be used as it is while its permissions are not PoolWatch's (then no other folder
            // is used); or a step that does not hold the one-instance mutex cannot use the folder as it stands, finds no
            // folder while a PoolWatch may hold that mutex, or cannot make it; or which folder the data folder is could not
            // be read once it was checked. No file was written to a data folder; the message also says what this start did
            // before (a folder moved aside, say).
            Console.Error.WriteLine(ex.Shown);
            tell.Box?.Invoke(ex.Shown, "PoolWatch: data folder cannot be used", MessageBoxIcon.Warning);
            return Engine.ExitCodes.DataNotSafe;
        }
        catch (Exception ex)
        {
            return Failed(tell, ex);
        }
    }

    /// <summary>Makes PoolWatch's own folder (where its program is) its current folder; left as it is when Windows refuses.</summary>
    internal static void UseOwnFolder()
    {
        try { Environment.CurrentDirectory = AppContext.BaseDirectory; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>
    /// Where a run that cannot go on says why: PoolWatch's log (opened only when there is something to say, and nothing
    /// when its data folder cannot be opened), the console, and a box. Only the tray app gets the box
    /// (<see cref="ShowsBoxes"/>); null for every other run.
    /// </summary>
    internal sealed record Telling(Action<string> Log, Action<string> Console, Action<string, string, MessageBoxIcon>? Box);

    /// <summary>The real places for this run: the log (one for the run, opened at the first message), standard error,
    /// and a message box for the tray app.</summary>
    internal static Telling TellingFor(CommandLineOptions opt, bool consoleMode)
    {
        var log = new Lazy<Action<string>>(() => TryOpenLog(opt));
        return new(message => log.Value(message), Console.Error.WriteLine, ShowsBoxes(opt, consoleMode) ? ShowBox : null);
    }

    /// <summary>
    /// Whether this run says why it stopped in a box: only the tray app, which has no console. Never a console mode, and
    /// never the token-leak guard's hidden helper, which runs beside a PoolWatch that goes on: a box there would hold
    /// PoolWatch.exe until someone clicked it.
    /// </summary>
    internal static bool ShowsBoxes(CommandLineOptions opt, bool consoleMode) => !consoleMode && opt.TokenLeakTest is null;

    private static void ShowBox(string text, string title, MessageBoxIcon icon)
    {
        try { MessageBox.Show(text, title, MessageBoxButtons.OK, icon); }
        catch { /* no desktop to show it on: the console and the log have it */ }
    }

    /// <summary>Never a silent exit: an error no step caught, on the console (in full), in the log and in the tray app's box. Exit 1.</summary>
    internal static int Failed(Telling tell, Exception ex)
    {
        tell.Console("PoolWatch failed: " + ex);
        tell.Log("PoolWatch failed: " + ex.Message);
        tell.Box?.Invoke("PoolWatch stopped because of an error: " + ex.Message, "PoolWatch: stopped", MessageBoxIcon.Error);
        return Engine.ExitCodes.Failed;
    }

    /// <summary>
    /// The tray app's and a headless run's start: takes the one-instance mutex named by <paramref name="mutexName"/> (<see
    /// cref="Engine.ExitSignal.AsTheOneInstance"/>), then runs while holding it, with the proof that this thread holds it.
    /// Another PoolWatch holding it: <paramref name="alreadyRunning"/>, exit 3. The private namespace the mutex lives in,
    /// or the mutex itself, could not be had: <paramref name="failed"/> says so with why (a box for the tray app, the
    /// console and the log), and the exit is 9; never a silent exit, never "already running". A failure once the start has
    /// the mutex is not one of these: it goes on to Main's own handling.
    /// </summary>
    internal static int RunAsTheOnlyMonitor(Func<string> mutexName, Func<Engine.ExitSignal.Hold, int> run, Action alreadyRunning, Action<string> failed)
    {
        bool taken = false;
        try
        {
            return Engine.ExitSignal.AsTheOneInstance(mutexName(), () =>
            {
                taken = true;
                alreadyRunning();
                return Engine.ExitCodes.AlreadyRunning;
            }, hold =>
            {
                taken = true;
                return run(hold);
            });
        }
        catch (Exception ex) when (!taken)
        {
            failed(NotStarted(ex));
            return Engine.ExitCodes.NamesNotSafe;
        }
    }

    /// <summary>What a failed start says: why, and that PoolWatch is not running.</summary>
    internal static string NotStarted(Exception ex) =>
        (ex is Engine.AdminNamespaceException ? ex.Message : $"PoolWatch could not take its one-instance name ({ex.Message}).") +
        " PoolWatch is not running.";

    /// <summary>
    /// The one-instance mutex's name, in PoolWatch's private namespace (throws when the namespace cannot be had): the one
    /// the setup's stop and the uninstall cleanup take too (<see cref="Engine.ExitSignal.StopAndHold(TimeSpan)"/>).
    /// </summary>
    private static string MonitorMutexName()
    {
        return Engine.AdminNamespace.Shared.MonitorMutexName;
    }

    /// <summary>A start that cannot go on: in PoolWatch's log, on the console, and in a box for the tray app.</summary>
    internal static void TellNotStarted(Telling tell, string message)
    {
        tell.Log(message);
        tell.Console(message);
        tell.Box?.Invoke(message, "PoolWatch: not started", MessageBoxIcon.Warning);
    }

    /// <summary>PoolWatch's log, or nothing when its data folder cannot be opened (the console still has the line).</summary>
    private static Action<string> TryOpenLog(CommandLineOptions opt)
    {
        try { return new Engine.AppPaths(opt.OutDir).OpenLog().Info; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PoolWatch's log could not be opened: " + ex.Message);
            return _ => { };
        }
    }

    /// <summary><c>--mark "note"</c>: drops a request for the running instance (it reads it on its next sample).</summary>
    private static int MarkRequest(CommandLineOptions opt)
    {
        string note = Core.Experiments.Marks.Clean(opt.Mark!);
        if (note.Length == 0) { Console.Error.WriteLine("--mark needs a note"); return Engine.ExitCodes.BadCommandLine; }
        var paths = new Engine.AppPaths(opt.OutDir);
        string dir = paths.EnsureDir(paths.RequestsDir);
        string path = paths.Guard.CheckReal(Path.Combine(dir, Core.Experiments.Marks.RequestName(DateTime.UtcNow, Environment.ProcessId)));
        File.WriteAllText(paths.Guard.CheckReal(path + ".tmp"), note);
        File.Move(paths.Guard.CheckReal(path + ".tmp"), path);
        Console.WriteLine("Mark queued: " + note);
        return Engine.ExitCodes.Done;
    }

    /// <summary>
    /// Exit 0: no clear task of your own (any more); 4: a clear task of your own is still there (you kept it, or the note
    /// says why), so the automatic clear must stay off.
    /// </summary>
    private static int Legacy(CommandLineOptions opt)
    {
        // First: a data folder PoolWatch refuses to use (AppPaths throws) stops this before any task is removed.
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
        return outcome == Engine.CacheHelper.LegacyOutcome.Kept ? Engine.ExitCodes.ClearTaskKept : 0;
    }

    /// <summary><c>--changes-dump &lt;file&gt;</c>: the Active changes list as JSON (CI and support).</summary>
    private static int ChangesDump(string file, CommandLineOptions opt)
    {
        var paths = new Engine.AppPaths(opt.OutDir);
        var changes = ChangeLedger.ForCommandLine(paths, paths.OpenLog()).List();
        File.WriteAllText(paths.Guard.CheckReal(file), ChangeLedger.ToJson(changes));
        Console.WriteLine($"changes written: {file} ({changes.Count})");
        return Engine.ExitCodes.Done;
    }

    /// <summary>
    /// <c>--stop-running</c>, run by the installer before it replaces the files: asks a running PoolWatch to exit through
    /// its exit signal, then ends the token-leak test helpers still running from this exe (<see cref="Engine.LeftoverHelpers"/>).
    /// 0: no PoolWatch runs now; 7: one still does; 9: its private names could not be opened, so it could not be asked.
    /// </summary>
    private static int StopRunning(CommandLineOptions opt)
    {
        var log = TryOpenLog(opt);
        return StopRunning(() => Engine.ExitSignal.StopAndHold(StopWait), () => EndLeftoverHelpers(log), log);
    }

    /// <summary>
    /// The stop with its steps passed in (the tests pass fakes). The helpers are ended only once PoolWatch has exited, while
    /// its one-instance mutex is held, so no PoolWatch can start a new one meanwhile.
    /// </summary>
    internal static int StopRunning(Func<IDisposable?> stopAndHold, Action endHelpers, Action<string> log)
    {
        IDisposable? held;
        try { held = stopAndHold(); }
        catch (Engine.AdminNamespaceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            log("stop running: " + ex.Message);
            return Engine.ExitCodes.NamesNotSafe;
        }
        using (held)
        {
            if (held is null)
            {
                Console.Error.WriteLine("PoolWatch is still running.");
                return Engine.ExitCodes.StillRunning;
            }
            endHelpers();
            return Engine.ExitCodes.Done;
        }
    }

    /// <summary>Ends the token-leak test helpers that still run from this PoolWatch.exe (after PoolWatch itself has exited).</summary>
    private static void EndLeftoverHelpers(Action<string> log) =>
        Engine.LeftoverHelpers.End(new Engine.WindowsProcessTable(), Environment.ProcessPath, Environment.ProcessId, log);

    /// <summary>
    /// <c>--uninstall-cleanup [--keep-changes] [--append-summary]</c>, run hidden by the uninstaller before it removes any
    /// file: stops a running PoolWatch through its exit signal, puts back what PoolWatch changed and writes
    /// uninstall-summary.txt in the data folder (which the uninstaller keeps unless every change was put back and you chose
    /// to delete it); with --append-summary, adds to it (<see cref="WriteSummary"/>). 0: done; 7: PoolWatch is still
    /// running and nothing was changed; 8: something could not be put back; 9: PoolWatch's private names could not be
    /// opened, so it could not be asked to exit and nothing was changed; 12: done (0 or 8) on the folder PoolWatch keeps its
    /// data in while it could not use the usual one (<see cref="CleanupExit"/>). Once PoolWatch has exited, the token-leak
    /// test helpers still running from this exe are ended first (<see cref="Engine.LeftoverHelpers"/>).
    /// </summary>
    private static int UninstallCleanup(CommandLineOptions opt)
    {
        // One list for the cleanup's steps, read once PoolWatch has stopped, so nothing it wrote on its way out is missed.
        ChangeLedger? ledger = null;
        ChangeLedger Ledger(Engine.AppPaths paths, Engine.Log log) => ledger ??= ChangeLedger.ForCommandLine(paths, log);
        return UninstallCleanup(opt.KeepChanges, () => Engine.ExitSignal.StopAndHold(StopWait), held => new Engine.AppPaths(opt.OutDir, held),
            (paths, log) => Ledger(paths, log).List(), (paths, log, which) => UninstallUndo(Ledger(paths, log), which),
            EndLeftoverHelpers, message => TryOpenLog(opt)(message), opt.AppendSummary);
    }

    /// <summary>
    /// Writes uninstall-summary.txt (through a file beside it, then moved over it). With <paramref name="append"/> (the
    /// uninstaller's second cleanup of the same uninstall, --append-summary), what the earlier cleanup wrote stays, and
    /// this one's summary follows it. A summary that cannot be written (the file, or the one beside it, is refused) is
    /// added to <see cref="KeptSummaryName"/> next to it, and the write throws, so the cleanup ends with exit code 1 and
    /// the uninstaller removes nothing; the next cleanup that writes the summary writes what is kept there first, then
    /// its own, and deletes that file. So the record of what a cleanup put back is not lost when a second cleanup adds
    /// to it, nor when a Retry after a failed write replaces it. <paramref name="real"/> checks each path
    /// (<c>PathGuard.CheckReal</c>: never through a link).
    /// </summary>
    internal static void WriteSummary(string file, string summary, bool append, Func<string, string> real)
    {
        string keptFile = Path.Combine(Path.GetDirectoryName(file) ?? "", KeptSummaryName);
        // What earlier cleanups could not write; "" when there is none, or when it cannot be read now (it then stays, for
        // a later cleanup to write).
        string kept = "";
        try { if (File.Exists(real(keptFile))) kept = File.ReadAllText(real(keptFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            string before = append && File.Exists(real(file)) ? File.ReadAllText(real(file)) : "";
            File.WriteAllText(real(file + ".tmp"), before + kept + summary);
            File.Move(real(file + ".tmp"), real(file), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.AppendAllText(real(keptFile), summary);
            throw;
        }
        // Written: what was kept is in the summary now. A kept file that cannot be deleted is written in again by the next
        // cleanup, and the uninstaller shows each line once.
        try { if (kept.Length > 0) File.Delete(real(keptFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The cleanup: PoolWatch is stopped and the one-instance mutex held first; only then are the data folder's paths made
    /// (<paramref name="makePaths"/>), on the thread that holds the mutex and with the proof the stop returned (<see
    /// cref="Engine.ExitSignal.Hold"/>), so their checks run as the one PoolWatch running and never move a folder a running
    /// tray app or headless run uses. Then the token-leak test helpers still running are ended (<paramref
    /// name="endHelpers"/>, with that folder's log), and uninstall-summary.txt goes into the data folder the paths settle
    /// on (added to with <paramref name="append"/>). The exit code is the one the uninstaller acts on (<see
    /// cref="CleanupExit"/>: 12 when that data folder is a folder of PoolWatch's own that it keeps its data in because it
    /// could not use the usual one). <paramref name="log"/> says why nothing was done when PoolWatch's private names could
    /// not be opened, before any data folder is touched. Tests pass the stop, the paths and the change steps.
    /// </summary>
    internal static int UninstallCleanup(bool keepChanges, Func<Engine.ExitSignal.Hold?> stopAndHold, Func<Engine.ExitSignal.Hold, Engine.AppPaths> makePaths,
        Func<Engine.AppPaths, Engine.Log, IReadOnlyList<ActiveChange>> list,
        Func<Engine.AppPaths, Engine.Log, Func<ActiveChange, bool>?, IReadOnlyList<(ActiveChange Change, UndoResult Result)>> undoAll,
        Action<Action<string>>? endHelpers = null, Action<string>? log = null, bool append = false)
    {
        Engine.ExitSignal.Hold? held = null;
        Engine.AppPaths? paths = null;
        Engine.Log? pathsLog = null;
        // Called by the steps below, which run only once the stop has worked and the mutex is held (held is not null).
        (Engine.AppPaths Paths, Engine.Log Log) Ready()
        {
            if (paths is null)
            {
                paths = makePaths(held!);
                pathsLog = paths.OpenLog();
            }
            return (paths, pathsLog!);
        }
        int code = UninstallCleanup(keepChanges, () => held = stopAndHold(), () => list(Ready().Paths, Ready().Log),
            which => undoAll(Ready().Paths, Ready().Log, which), summary =>
            {
                var (p, l) = Ready();
                Console.Write(summary);
                l.Info("uninstall cleanup: " + summary.TrimEnd().Replace(Environment.NewLine, "; "));
                WriteSummary(Path.Combine(p.DataRoot, UninstallSummaryName), summary, append, p.Guard.CheckReal);
            }, endHelpers is null ? null : () => endHelpers(Ready().Log.Info), log, append);
        return CleanupExit(code, paths?.UsualRootNotUsed is not null);
    }

    /// <summary>
    /// The cleanup's exit code as the uninstaller gets it: when PoolWatch kept its data in a folder of its own because it
    /// could not use the usual one, a cleanup that ran (0 or 8) is <see cref="Engine.ExitCodes.OtherDataFolder"/>, so the uninstaller
    /// neither reads the summary nor deletes anything in the usual folder, which PoolWatch did not use this time.
    /// </summary>
    internal static int CleanupExit(int code, bool otherDataFolder) =>
        otherDataFolder && code is 0 or Engine.ExitCodes.NotAllPutBack ? Engine.ExitCodes.OtherDataFolder : code;

    /// <summary>
    /// The cleanup with its steps passed in (the tests pass fakes). The one-instance mutex is held from the stop to the
    /// end, so no PoolWatch can start while its changes are being put back. <paramref name="again"/>: a second cleanup of
    /// the same uninstall, whose summary says so (<see cref="UninstallSummary"/>).
    /// </summary>
    internal static int UninstallCleanup(bool keepChanges, Func<IDisposable?> stopAndHold, Func<IReadOnlyList<ActiveChange>> list,
        Func<Func<ActiveChange, bool>?, IReadOnlyList<(ActiveChange Change, UndoResult Result)>> undoAll, Action<string> report,
        Action? endHelpers = null, Action<string>? log = null, bool again = false)
    {
        IDisposable? stopped;
        try { stopped = stopAndHold(); }
        catch (Engine.AdminNamespaceException ex)
        {
            Console.Error.WriteLine(ex.Message + " Nothing was changed.");
            log?.Invoke("uninstall cleanup: " + ex.Message + " Nothing was changed.");
            return Engine.ExitCodes.NamesNotSafe;
        }
        using var held = stopped;
        if (held is null)
        {
            Console.Error.WriteLine("PoolWatch is still running: nothing was changed.");
            return Engine.ExitCodes.StillRunning;
        }
        endHelpers?.Invoke();
        var which = UndoSelection(keepChanges);
        var listed = list();
        var results = undoAll(which);
        report(UninstallSummary(listed, which, results, DateTime.Now, again));
        return results.All(r => r.Result.Done) ? 0 : Engine.ExitCodes.NotAllPutBack;
    }

    /// <summary>
    /// The uninstaller's Undo all. Never confirmed: no one is there to accept a warning, so a clear task that runs RAMMap
    /// from a folder you can change is not put back from here (its definition is saved for import, and the run exits 8).
    /// </summary>
    internal static IReadOnlyList<(ActiveChange Change, UndoResult Result)> UninstallUndo(ChangeLedger ledger, Func<ActiveChange, bool>? which) =>
        ledger.UndoAll(which, confirmed: false);

    /// <summary>
    /// Which changes the uninstaller tries to put back: every one (null; one that cannot be put back stays, and the
    /// summary says why), or with --keep-changes only the logon task and the desktop shortcut, which would otherwise
    /// start files that are about to be deleted.
    /// </summary>
    internal static Func<ActiveChange, bool>? UndoSelection(bool keepChanges) =>
        keepChanges ? c => c.Kind is ChangeKind.LogonTask or ChangeKind.Shortcut : null;

    /// <summary>
    /// uninstall-summary.txt: each change put back, put back only in part (and what was not done), not put back (and why),
    /// or kept, in the uninstaller's words. A change put back with more to say (<see cref="UndoResult.Caveat"/>: a service
    /// whose start type is back but that was not started again, say) is a "Partly put back:" line, never a "Put back:"
    /// one: the uninstaller's notices list those lines under a heading of their own, ahead of the rest. A second cleanup of
    /// the same uninstall (<paramref name="again"/>), whose lines follow the first's, says so in its first line.
    /// </summary>
    internal static string UninstallSummary(IReadOnlyList<ActiveChange> listed, Func<ActiveChange, bool>? which,
        IReadOnlyList<(ActiveChange Change, UndoResult Result)> results, DateTime now, bool again = false)
    {
        var text = new System.Text.StringBuilder().AppendLine(again
            ? $"PoolWatch uninstall, cleanup run again, {now:yyyy-MM-dd HH:mm}"
            : $"PoolWatch uninstall, {now:yyyy-MM-dd HH:mm}");
        foreach (var (change, result) in results)
            text.AppendLine(!result.Done ? $"Could not put back: {change.What}. {result.Message}"
                : result.Caveat is { Length: > 0 } c ? $"Partly put back: {change.What}. {Sentence(c)}"
                : "Put back: " + change.What);
        foreach (var change in listed.Where(c => which is not null && !which(c)))
            text.AppendLine("Kept: " + change.What);
        if (results.Count == 0 && listed.Count == 0)
            text.AppendLine(again ? "PoolWatch had made no more changes to put back." : "PoolWatch had made no changes to put back.");
        return text.ToString();
    }

    /// <summary>A clause as a sentence: its first letter in capitals, and a full stop at its end unless it has one.</summary>
    private static string Sentence(string clause) =>
        char.ToUpperInvariant(clause[0]) + clause[1..] + (clause.EndsWith('.') ? "" : ".");

    /// <summary>Background services: dump / turn off / restore from the command line.</summary>
    private static int Services(CommandLineOptions opt)
    {
        try
        {
            var paths = new Engine.AppPaths(opt.OutDir);
            var log = paths.OpenLog();
            // No sampler runs here: process costs are not known (the Cost column and "saved" need the running app).
            var ctl = new Engine.ServicesController(paths, log, f => Console.WriteLine(f.Title), _ => null, _ => null);
            return Services(ctl, opt.ServicesDump, opt.ServiceOff, opt.ServiceRestore, Console.Out);
        }
        catch (Exception ex) { Console.Error.WriteLine("services failed: " + ex.Message); return Engine.ExitCodes.Failed; }
    }

    /// <summary>
    /// The services steps with the controller passed in (the tests pass one over a fake backend): writes each result line
    /// to <paramref name="output"/>. 0: everything asked for was done (a service off already counts as done, and so does
    /// "all" when nothing is recorded); <see cref="Engine.ExitCodes.NotAllPutBack"/> when something was not put back, or no change of
    /// the service named is recorded; else <see cref="Engine.ExitCodes.NotAllTurnedOff"/> when something was not turned off.
    /// </summary>
    internal static int Services(Engine.ServicesController ctl, string? dump, string? off, string? restore, TextWriter output)
    {
        bool notOff = false, notBack = false;
        if (dump is { } file) { ctl.Dump(file); output.WriteLine("services written: " + file); }
        if (off is { } name)
            foreach (var (s, e) in ctl.Apply([name], "command line"))
            {
                output.WriteLine(e is null ? $"{s}: off" : $"{s}: {e}");
                notOff |= e is not null && e != Engine.ServicesController.OffAlready;
            }
        if (restore is { } r)
        {
            bool all = r.Equals("all", StringComparison.OrdinalIgnoreCase);
            var results = ctl.Restore(all ? null : r, out var putBack);
            foreach (var (s, e) in results)
            {
                output.WriteLine(e is null ? $"{s}: restored" : $"{s}: {e}");
                // Put back with more to say (not started again, say) is put back: its record is gone.
                notBack |= e is not null && !putBack.Any(c => c.Service.Equals(s, StringComparison.OrdinalIgnoreCase));
            }
            // Nothing recorded to put back: said as the Services tab says it. For a name, that exits as not put back, so a
            // name typed wrong never reads as done.
            if (results.Count == 0) output.WriteLine(Ui.ServicesPageText.NothingToRestoreText(all ? null : r));
            if (results.Count == 0 && !all) notBack = true;
        }
        return notBack ? Engine.ExitCodes.NotAllPutBack : notOff ? Engine.ExitCodes.NotAllTurnedOff : 0;
    }

    /// <summary>
    /// The Clear cache steps from an elevated console. There is nothing to set up any more (the purge is
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
                    return Engine.ExitCodes.Done;
                case "enable-auto": // turning the automatic clear on: only a clear task of your own (removed on a yes)
                case "remove-legacy": return Legacy(opt);
            }
            return Engine.ExitCodes.BadCommandLine;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Clear cache step failed: " + ex.Message);
            return Engine.ExitCodes.Failed;
        }
    }
}
