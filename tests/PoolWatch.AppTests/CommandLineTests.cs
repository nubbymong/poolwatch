using PoolWatch.Engine.Changes;
using System.Security.AccessControl;
using PoolWatch.Engine;
using PoolWatch.Headless;

namespace PoolWatch.AppTests;

/// <summary>
/// The installer's and the uninstaller's command-line options, and the uninstall cleanup with a fake stop step and a fake
/// change ledger (nothing on the PC is stopped or changed).
/// </summary>
public sealed class CommandLineTests
{
    private static readonly ActiveChange Logon = new(IdOf(ChangeKind.LogonTask), ChangeKind.LogonTask, null, "PoolWatch starts when you sign in", true, null);
    private static readonly ActiveChange Shortcut = new(IdOf(ChangeKind.Shortcut), ChangeKind.Shortcut, DateTime.UtcNow, "Created the desktop shortcut \"ChatGPT (no shader cache)\"", true, null);
    private static readonly ActiveChange Fax = new(IdOf(ChangeKind.Service), ChangeKind.Service, DateTime.UtcNow, "Turned off Fax (Fax)", true, null);
    private static readonly ActiveChange Hourly = new(IdOf(ChangeKind.AutoClear), ChangeKind.AutoClear, null, "The cache is cleared every hour", true, null);
    private static readonly ActiveChange[] All = [Logon, Shortcut, Fax, Hourly];

    private static string IdOf(ChangeKind kind) => kind.ToString().ToLowerInvariant();

    [Fact]
    public void TheNewOptionsParse()
    {
        Assert.True(CommandLineOptions.Parse(["--stop-running"]).StopRunning);
        var cleanup = CommandLineOptions.Parse(["--uninstall-cleanup", "--keep-changes"]);
        Assert.True(cleanup.UninstallCleanup);
        Assert.True(cleanup.KeepChanges);
        Assert.Empty(cleanup.Errors);
        Assert.False(CommandLineOptions.Parse(["--uninstall-cleanup"]).KeepChanges);
        Assert.Equal(@"C:\out\changes.json", CommandLineOptions.Parse(["--changes-dump", @"C:\out\changes.json"]).ChangesDump);
    }

    [Fact]
    public void KeepChangesOnItsOwnIsAnError() =>
        Assert.Contains("--keep-changes goes with --uninstall-cleanup", CommandLineOptions.Parse(["--keep-changes"]).Errors);

    [Fact]
    public void AppendSummaryGoesOnlyWithTheCleanup()
    {
        var cleanup = CommandLineOptions.Parse(["--uninstall-cleanup", "--keep-changes", "--append-summary"]);
        Assert.True(cleanup.AppendSummary);
        Assert.Empty(cleanup.Errors);
        Assert.False(CommandLineOptions.Parse(["--uninstall-cleanup"]).AppendSummary);
        Assert.Contains("--append-summary goes with --uninstall-cleanup", CommandLineOptions.Parse(["--append-summary"]).Errors);
    }

    [Fact]
    public void ChangesDumpNeedsAFile() =>
        Assert.Contains("--changes-dump needs a value", CommandLineOptions.Parse(["--changes-dump"]).Errors);

    [Fact]
    public void TheUsageListsTheNewOptions()
    {
        foreach (var option in new[] { "--stop-running", "--uninstall-cleanup", "--keep-changes", "--append-summary", "--changes-dump", @"app\PoolWatch.exe" })
            Assert.Contains(option, CommandLineOptions.Usage);
        // The setup no longer has a logon task step: a task made before 1.0 names the launcher's place already.
        Assert.DoesNotContain("--update-logon-task", CommandLineOptions.Usage);
        Assert.Contains("unknown option '--update-logon-task'", CommandLineOptions.Parse(["--update-logon-task"]).Errors);
        Assert.DoesNotContain("PoolWatchLauncher", CommandLineOptions.Usage);
    }

    [Fact]
    public void ASecondCleanupAddsToTheSummaryAndTheFirstReplacesIt()
    {
        // The uninstaller runs the cleanup a second time when part of PoolWatch still runs after the first; that run adds
        // to uninstall-summary.txt (--append-summary), so what the first put back stays on record. A first run replaces
        // the file, so an earlier uninstall's record is never read as this one's.
        string dir = Path.Combine(Path.GetTempPath(), "pw-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "uninstall-summary.txt");
            var real = new List<string>();
            Func<string, string> check = p => { real.Add(p); return p; };
            File.WriteAllText(file, "PoolWatch uninstall, 2026-10-01 09:00" + Environment.NewLine + "Put back: an older uninstall's change" + Environment.NewLine);
            Program.WriteSummary(file, "first" + Environment.NewLine, append: false, check);
            Assert.Equal("first" + Environment.NewLine, File.ReadAllText(file));
            Program.WriteSummary(file, "second" + Environment.NewLine, append: true, check);
            Assert.Equal("first" + Environment.NewLine + "second" + Environment.NewLine, File.ReadAllText(file));
            Assert.False(File.Exists(file + ".tmp"));
            // Every path it touches goes through the check (PathGuard.CheckReal in PoolWatch: never through a link).
            Assert.Contains(file, real);
            Assert.Contains(file + ".tmp", real);
            // Appending with no file yet writes this run's summary alone.
            File.Delete(file);
            Program.WriteSummary(file, "only" + Environment.NewLine, append: true, check);
            Assert.Equal("only" + Environment.NewLine, File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
        // A second run's summary says it is one, and an empty second run says nothing more was to be put back.
        string again = Program.UninstallSummary([], null, [], new DateTime(2026, 11, 1, 9, 0, 0), again: true);
        Assert.Equal("PoolWatch uninstall, cleanup run again, 2026-11-01 09:00" + Environment.NewLine + "PoolWatch had made no more changes to put back." + Environment.NewLine, again);
        string first = Program.UninstallSummary([], null, [], new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.StartsWith("PoolWatch uninstall, 2026-11-01 09:00" + Environment.NewLine, first);
        // The cleanup passes "again" on to its summary.
        string? summary = null;
        Program.UninstallCleanup(false, () => new Release(() => { }), () => [], _ => [], s => summary = s, again: true);
        Assert.StartsWith("PoolWatch uninstall, cleanup run again, ", summary);
    }

    /// <summary>
    /// A second cleanup of the same uninstall (it adds to the summary) whose write cannot be done keeps its summary beside
    /// the file; the next one that adds to it writes, after what the first wrote, what was kept and then its own, so the
    /// summary reads in the order the cleanups ran.
    /// </summary>
    [Fact]
    public void ACleanupThatAddsToTheSummaryWritesWhatWasKeptAfterWhatIsThereAndBeforeItsOwn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, Program.UninstallSummaryName), kept = Path.Combine(dir, Program.KeptSummaryName);
            string first = "PoolWatch uninstall, 2026-11-05 09:00" + Environment.NewLine + "Put back: Turned off Example Service" + Environment.NewLine;
            string second = "PoolWatch uninstall, 2026-11-05 09:01" + Environment.NewLine + "Put back: The cache is cleared every hour" + Environment.NewLine;
            string third = "PoolWatch uninstall, 2026-11-05 09:02" + Environment.NewLine + "PoolWatch had made no changes to put back." + Environment.NewLine;
            Program.WriteSummary(file, first, append: false, p => p);
            using (var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Assert.True(Record.Exception(() => Program.WriteSummary(file, second, append: true, p => p)) is IOException or UnauthorizedAccessException,
                    "the write that cannot be done throws");
            Assert.Equal(second, File.ReadAllText(kept));
            Program.WriteSummary(file, third, append: true, p => p);
            Assert.Equal(first + second + third, File.ReadAllText(file));
            Assert.False(File.Exists(kept), "what was kept is in the summary now");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A summary that cannot be written (here refused by this test, on its own temp file) is kept beside it and the write
    /// throws, so the cleanup ends with exit code 1 and the uninstaller removes nothing. The next cleanup, a Retry that
    /// replaces the file, writes what was kept first and then its own, and the kept file goes: the record of what the
    /// first run put back is not lost.
    /// </summary>
    [Fact]
    public void ASummaryThatCannotBeWrittenIsKeptAndTheNextCleanupWritesItFirst()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, Program.UninstallSummaryName), kept = Path.Combine(dir, Program.KeptSummaryName);
            string first = "PoolWatch uninstall, 2026-11-05 09:00" + Environment.NewLine + "Put back: Turned off Example Service" + Environment.NewLine;
            using (var holder = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                var thrown = Record.Exception(() => Program.WriteSummary(file, first, append: false, p => p));
                Assert.True(thrown is IOException or UnauthorizedAccessException, $"the write that cannot be done throws ({thrown})");
            }
            Assert.Equal(first, File.ReadAllText(kept));
            Assert.Equal(0, new FileInfo(file).Length);
            string retry = "PoolWatch uninstall, 2026-11-05 09:01" + Environment.NewLine + "PoolWatch had made no changes to put back." + Environment.NewLine;
            Program.WriteSummary(file, retry, append: false, p => p);
            Assert.Equal(first + retry, File.ReadAllText(file));
            Assert.False(File.Exists(kept), "what was kept is in the summary now");
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
        // A summary that cannot be written is not taken for a cleanup that did all it was asked: the cleanup throws, and the
        // program's start turns what a step throws into exit code 1 (Program.Failed).
        Assert.Throws<IOException>(() => Program.UninstallCleanup(false, () => new Release(() => { }), () => [], _ => [], _ => throw new IOException("refused")));
        Assert.Equal(1, Program.Failed(new Program.Telling(_ => { }, _ => { }, null), new IOException("refused")));
        // The docs say so, in these words (line breaks left out); a sentence that is changed turns this red.
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        Assert.Contains("writes `uninstall-summary.txt` (a second cleanup adds to it; a summary that cannot be written there is kept beside it, in " +
                        "`uninstall-summary-kept.txt`, the cleanup ends with exit code 1 so nothing is removed, and the next cleanup writes it in first);",
            Doc("SECURITY.md"));
        Assert.Contains("a summary a run could not write there is kept beside it, in `uninstall-summary-kept.txt`, that run ends with exit code 1, and " +
                        "the next run writes it in first, so a Retry that replaces the file keeps what the failed run put back", Doc("docs", "DESIGN.md"));
        Assert.Contains("`uninstall-summary.txt` (and `uninstall-summary-kept.txt` while a cleanup's summary could not be written there: the next " +
                        "cleanup writes it in)", Doc("docs", "TECHNICAL.md"));
        Assert.Equal("uninstall-summary-kept.txt", Program.KeptSummaryName);
    }

    [Fact]
    public void KeepingTheChangesStillUndoesTheLogonTaskAndTheShortcut()
    {
        var which = Program.UndoSelection(keepChanges: true);
        Assert.NotNull(which);
        Assert.Equal([Logon, Shortcut], All.Where(which));
        Assert.Null(Program.UndoSelection(keepChanges: false)); // everything
    }

    [Fact]
    public void StillRunningChangesNothing()
    {
        bool listed = false, undone = false, reported = false;

        int exit = Program.UninstallCleanup(false, () => null, () => { listed = true; return All; },
            _ => { undone = true; return []; }, _ => reported = true);

        Assert.Equal(Engine.ExitCodes.StillRunning, exit);
        Assert.False(listed || undone || reported);
    }

    [Fact]
    public void EverythingUndoneIsExitZeroAndTheMutexIsHeldUntilTheEnd()
    {
        var steps = new List<string>();
        string? summary = null;

        int exit = Program.UninstallCleanup(false, () => { steps.Add("stopped"); return new Release(() => steps.Add("released")); },
            () => { steps.Add("listed"); return All; },
            which => { steps.Add("undone"); return Undo(which, _ => true); },
            s => { steps.Add("reported"); summary = s; });

        Assert.Equal(0, exit);
        Assert.Equal(["stopped", "listed", "undone", "reported", "released"], steps);
        Assert.Contains("Put back: Turned off Fax (Fax)", summary);
        Assert.Contains("Put back: PoolWatch starts when you sign in", summary);
        Assert.DoesNotContain("Kept", summary);
    }

    [Fact]
    public void SomethingNotPutBackIsExitEightAndSaysWhy()
    {
        string? summary = null;

        int exit = Program.UninstallCleanup(false, () => new Release(() => { }), () => All,
            which => Undo(which, c => c != Fax), s => summary = s);

        Assert.Equal(Engine.ExitCodes.NotAllPutBack, exit);
        Assert.Contains("Could not put back: Turned off Fax (Fax). PoolWatch could not reach the service manager.", summary);
    }

    [Fact]
    public void KeptChangesAreListedAsKept()
    {
        string? summary = null;
        Func<ActiveChange, bool>? asked = null;

        int exit = Program.UninstallCleanup(true, () => new Release(() => { }), () => All,
            which => { asked = which; return Undo(which, _ => true); }, s => summary = s);

        Assert.Equal(0, exit);
        Assert.NotNull(asked);
        Assert.Contains("Put back: Created the desktop shortcut", summary);
        Assert.Contains("Kept: Turned off Fax (Fax)", summary);
        Assert.Contains("Kept: The cache is cleared every hour", summary);
        Assert.DoesNotContain("Put back: Turned off Fax", summary);
    }

    [Fact]
    public void NothingToUndoIsSaidPlainly()
    {
        string? summary = null;

        int exit = Program.UninstallCleanup(false, () => new Release(() => { }), () => [], _ => [], s => summary = s);

        Assert.Equal(0, exit);
        Assert.Contains("PoolWatch had made no changes to put back.", summary);
    }

    // ---------------------------------------------------------------- PoolWatch's private names, and the leftover helpers

    private static readonly Engine.AdminNamespaceException NoNames = new("PoolWatch could not create its private names (Windows error 5: Access is denied.). It needs to run as administrator.");

    [Fact]
    public void ACleanupThatCannotOpenThePrivateNamesChangesNothingAndSaysSo()
    {
        bool listed = false, undone = false, reported = false, ended = false;
        var log = new List<string>();

        int exit = Program.UninstallCleanup(false, () => throw NoNames, () => { listed = true; return All; },
            _ => { undone = true; return []; }, _ => reported = true, () => ended = true, log.Add);

        Assert.Equal(Engine.ExitCodes.NamesNotSafe, exit); // a failure to the uninstaller, which then removes no file
        Assert.False(listed || undone || reported || ended);
        Assert.Contains(log, l => l.Contains("Windows error 5") && l.Contains("Nothing was changed."));
    }

    [Fact]
    public void TheCleanupEndsTheLeftoverHelpersOncePoolWatchHasStoppedAndBeforeItPutsBack()
    {
        var steps = new List<string>();

        int exit = Program.UninstallCleanup(false, () => { steps.Add("stopped"); return new Release(() => steps.Add("released")); },
            () => { steps.Add("listed"); return All; }, which => { steps.Add("undone"); return Undo(which, _ => true); },
            _ => steps.Add("reported"), () => steps.Add("helpers ended"));

        Assert.Equal(0, exit);
        Assert.Equal(["stopped", "helpers ended", "listed", "undone", "reported", "released"], steps);
        // Still running: PoolWatch's helpers are its own, so none is ended.
        steps.Clear();
        Assert.Equal(Engine.ExitCodes.StillRunning, Program.UninstallCleanup(false, () => null, () => All, _ => [], _ => { }, () => steps.Add("helpers ended")));
        Assert.Empty(steps);
    }

    [Fact]
    public void TheSetupsStopEndsTheLeftoverHelpersWhileItHoldsTheMutex()
    {
        var steps = new List<string>();

        int exit = Program.StopRunning(() => { steps.Add("stopped"); return new Release(() => steps.Add("released")); }, () => steps.Add("helpers ended"), steps.Add);

        Assert.Equal(0, exit);
        Assert.Equal(["stopped", "helpers ended", "released"], steps);
    }

    [Fact]
    public void TheSetupsStopSaysStillRunningOrThatItCouldNotAskAndEndsNothing()
    {
        var steps = new List<string>();

        Assert.Equal(Engine.ExitCodes.StillRunning, Program.StopRunning(() => null, () => steps.Add("helpers ended"), steps.Add));
        Assert.Empty(steps);

        Assert.Equal(Engine.ExitCodes.NamesNotSafe, Program.StopRunning(() => throw NoNames, () => steps.Add("helpers ended"), steps.Add));
        Assert.Equal(["stop running: " + NoNames.Message], steps);
    }

    [Fact]
    public void AStartWhosePrivateNamesCannotBeOpenedSaysSoAndIsNeverAlreadyRunning()
    {
        // A program without administrator rights cannot take the names, so a failure here is never "already running": it
        // says why (Main passes TellNotStarted, which the tests below hold) and exits 9.
        string? said = null;
        bool ran = false, already = false;

        int exit = Program.RunAsTheOnlyMonitor(() => throw NoNames, _ => { ran = true; return 0; }, () => already = true, m => said = m);

        Assert.Equal(Engine.ExitCodes.NamesNotSafe, exit);
        Assert.False(ran || already);
        Assert.Equal(NoNames.Message + " PoolWatch is not running.", said);

        // The name refused for another reason: also said, with why.
        exit = Program.RunAsTheOnlyMonitor(() => throw new UnauthorizedAccessException("Access to the path is denied."), _ => 0, () => already = true, m => said = m);
        Assert.Equal(Engine.ExitCodes.NamesNotSafe, exit);
        Assert.False(already);
        Assert.Equal("PoolWatch could not take its one-instance name (Access to the path is denied.). PoolWatch is not running.", said);

        // The mutex itself refused (one whose rules let no one open it, a mutex of the test's own): said, never "already
        // running", and nothing runs.
        string name = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");
        var noOne = new MutexSecurity();
        noOne.SetSecurityDescriptorSddlForm("D:P");
        using (MutexAcl.Create(false, name, out bool created, noOne))
        {
            Assert.True(created);
            said = null;
            exit = Program.RunAsTheOnlyMonitor(() => name, _ => { ran = true; return 0; }, () => already = true, m => said = m);
            Assert.Equal(Engine.ExitCodes.NamesNotSafe, exit);
            Assert.False(ran || already);
            Assert.StartsWith("PoolWatch could not take its one-instance name (", said);
            Assert.EndsWith("). PoolWatch is not running.", said);
        }
    }

    [Fact]
    public void AnotherPoolWatchHoldingTheMutexIsAlreadyRunningAndTheFirstRunsWhileHoldingIt()
    {
        // A mutex of the test's own, never the real one: held by another thread, the start is "already running" and runs
        // nothing; free, it runs while holding it, with the proof that its thread holds it, and lets it go after.
        string name = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");
        var steps = new List<string>();
        using (var holding = new ManualResetEventSlim())
        using (var stop = new ManualResetEventSlim())
        {
            var other = new Thread(() =>
            {
                using var mutex = new Mutex(true, name);
                holding.Set();
                stop.Wait(TimeSpan.FromSeconds(30));
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            other.Start();
            Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));
            int exit = Program.RunAsTheOnlyMonitor(() => name, _ => { steps.Add("ran"); return 0; },
                () => steps.Add("already running"), m => steps.Add("failed: " + m));
            Assert.Equal(Engine.ExitCodes.AlreadyRunning, exit);
            Assert.Equal(["already running"], steps);
            stop.Set();
            Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        }

        steps.Clear();
        int ran = Program.RunAsTheOnlyMonitor(() => name, hold =>
        {
            steps.Add(hold.HeldHere && ExitSignal.InstanceRunning(name) ? "ran holding it" : "ran without it");
            return 42;
        }, () => steps.Add("already running"), m => steps.Add("failed: " + m));
        Assert.Equal(42, ran);
        Assert.Equal(["ran holding it"], steps);
        Assert.False(ExitSignal.InstanceRunning(name)); // let go once the run is over

        // A failure once the start has the mutex is not a start that could not take it: it goes on to Main's own handling.
        Assert.Throws<InvalidOperationException>(() =>
            Program.RunAsTheOnlyMonitor(() => name, _ => throw new InvalidOperationException("the run failed"), () => { }, m => steps.Add("failed: " + m)));
        Assert.DoesNotContain(steps, s => s.StartsWith("failed", StringComparison.Ordinal));
    }

    /// <summary>A fake Undo all: every change the selection takes, done when <paramref name="works"/> says so.</summary>
    private static IReadOnlyList<(ActiveChange Change, UndoResult Result)> Undo(Func<ActiveChange, bool>? which, Func<ActiveChange, bool> works) =>
        All.Where(c => which?.Invoke(c) ?? true)
            .Select(c => (c, works(c) ? new UndoResult(true, "put back") : new UndoResult(false, "PoolWatch could not reach the service manager.")))
            .ToList();

    private sealed class Release(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void TheUsageSaysWhatTheSetupsAndTheUninstallersStepsDoAndEachExitTheyGive()
    {
        string usage = System.Text.RegularExpressions.Regex.Replace(CommandLineOptions.Usage, @"\s+", " ");
        Assert.Contains($"--stop-running (elevated; run by the setup) ask a running PoolWatch to exit and wait up to 60 s, then end the token-leak " +
                        $"test helpers still running from this exe (exit {Engine.ExitCodes.StillRunning}: still running; {Engine.ExitCodes.NamesNotSafe}: its " +
                        "private names could not be opened)", usage);
        Assert.Contains("--uninstall-cleanup [--keep-changes] [--append-summary] (elevated; run by the uninstaller) stop PoolWatch, end the token-leak " +
                        "test helpers still running from this exe, put back what it changed (--keep-changes: only the logon task and the desktop " +
                        "shortcut) and write uninstall-summary.txt in the data folder (--append-summary: add to it, after an earlier cleanup of " +
                        "the same uninstall)", usage);
        Assert.Contains($"(exit {Engine.ExitCodes.DataNotSafe}: its data folder could not be made safe, nothing changed; {Engine.ExitCodes.StillRunning}: still " +
                        $"running, nothing changed; {Engine.ExitCodes.NotAllPutBack}: something was not put back; {Engine.ExitCodes.NamesNotSafe}: its private " +
                        $"names could not be opened, nothing changed; {Engine.ExitCodes.OtherDataFolder}: done on a folder of its own, used in place of the usual " +
                        "data folder)", usage);
        // The services steps' and the clear-task step's codes, from the same table.
        Assert.Contains($"--service-off <name> (elevated) turn an optional/third-party service off (gated) (exit {Engine.ExitCodes.NotAllTurnedOff}: not turned off, or " +
                        "not stopped; the line says why)", usage);
        Assert.Contains($"(exit {Engine.ExitCodes.NotAllPutBack}: something was not put back, or no change of it is recorded; the line says why)", usage);
        Assert.Contains($"(exit {Engine.ExitCodes.ClearTaskKept}: kept, so the automatic clear stays off)", usage);
    }

    // ---------------------------------------------------------------- never a silent exit

    /// <summary>What a run said, and where: its log, the console, and each box (text, title, icon).</summary>
    private sealed class Said
    {
        public List<string> Log { get; } = [];
        public List<string> Console { get; } = [];
        public List<(string Text, string Title, MessageBoxIcon Icon)> Boxes { get; } = [];

        public Program.Telling Telling(bool box) => new(Log.Add, Console.Add, box ? (text, title, icon) => Boxes.Add((text, title, icon)) : null);
    }

    private static readonly string[] Helper = ["--token-leak-test", "64", @"C:\ProgramData\PoolWatch\cache\token-test-1.json"];

    [Fact]
    public void AStartThatCannotGoOnSaysWhyInTheLogOnTheConsoleAndInTheTrayAppsBox()
    {
        var tray = new Said();
        Program.TellNotStarted(tray.Telling(box: true), NoNames.Message);
        Assert.Equal([NoNames.Message], tray.Log);
        Assert.Equal([NoNames.Message], tray.Console);
        Assert.Equal([(NoNames.Message, "PoolWatch: not started", MessageBoxIcon.Warning)], tray.Boxes);

        // A run with a console, or the hidden helper: the log and the console, never a box.
        var other = new Said();
        Program.TellNotStarted(other.Telling(box: false), NoNames.Message);
        Assert.Equal([NoNames.Message], other.Log);
        Assert.Equal([NoNames.Message], other.Console);
        Assert.Empty(other.Boxes);
    }

    [Fact]
    public void AnErrorNoStepCaughtIsNeverASilentExit()
    {
        var error = new InvalidOperationException("the disk went away");
        var tray = new Said();

        Assert.Equal(1, Program.Failed(tray.Telling(box: true), error));

        Assert.Equal(["PoolWatch failed: " + error], tray.Console); // in full, with where it happened
        Assert.Equal(["PoolWatch failed: the disk went away"], tray.Log);
        Assert.Equal([("PoolWatch stopped because of an error: the disk went away", "PoolWatch: stopped", MessageBoxIcon.Error)], tray.Boxes);

        var helper = new Said();
        Assert.Equal(1, Program.Failed(helper.Telling(box: false), error));
        Assert.Single(helper.Log);
        Assert.Single(helper.Console);
        Assert.Empty(helper.Boxes);
    }

    [Fact]
    public void OnlyTheTrayAppSaysWhyInABox()
    {
        // The tray app has no console. A console mode has one; the token-leak guard's hidden helper runs beside a PoolWatch
        // that goes on, and a box there would hold PoolWatch.exe until someone clicked it.
        Assert.True(Program.ShowsBoxes(CommandLineOptions.Parse([]), consoleMode: false));
        Assert.False(Program.ShowsBoxes(CommandLineOptions.Parse(["--headless"]), consoleMode: true));
        Assert.False(Program.ShowsBoxes(CommandLineOptions.Parse(["--stop-running"]), consoleMode: true));
        Assert.False(Program.ShowsBoxes(CommandLineOptions.Parse(Helper), consoleMode: false));

        Assert.NotNull(Program.TellingFor(CommandLineOptions.Parse([]), consoleMode: false).Box);
        Assert.Null(Program.TellingFor(CommandLineOptions.Parse(Helper), consoleMode: false).Box);
        Assert.Null(Program.TellingFor(CommandLineOptions.Parse(["--headless"]), consoleMode: true).Box);
    }

    [Fact]
    public void MainSaysWhyThroughTheseStepsAndItsBoxIsAMessageBox()
    {
        // Main cannot run here (it would start PoolWatch), and a real box would wait for a click: their wiring is held to
        // Program.cs's own text, with every run of white space made one space.
        string program = null!;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && program is null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx")))
                program = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(dir.FullName, "src", "PoolWatch", "Program.cs")), @"\s+", " ");
        Assert.Contains("var tell = TellingFor(opt, consoleMode); try {", program);
        Assert.Contains("message => TellNotStarted(tell, message)); }", program);
        Assert.Contains("catch (Exception ex) { return Failed(tell, ex); } }", program);
        Assert.Contains("Console.Error.WriteLine(ex.Shown); tell.Box?.Invoke(ex.Shown, \"PoolWatch: data folder cannot be used\", MessageBoxIcon.Warning); " +
                        "return Engine.ExitCodes.DataNotSafe; }", program);
        // One log for the run, opened at the first message (a new Log per message would start a new file per message
        // while the log is held).
        Assert.Contains("var log = new Lazy<Action<string>>(() => TryOpenLog(opt)); " +
                        "return new(message => log.Value(message), Console.Error.WriteLine, ShowsBoxes(opt, consoleMode) ? ShowBox : null);", program);
        Assert.Contains("private static void ShowBox(string text, string title, MessageBoxIcon icon) { try { MessageBox.Show(text, title, MessageBoxButtons.OK, icon); }", program);
    }
}
