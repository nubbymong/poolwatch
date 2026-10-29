using PoolWatch.Engine.Changes;
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
    public void ChangesDumpNeedsAFile() =>
        Assert.Contains("--changes-dump needs a value", CommandLineOptions.Parse(["--changes-dump"]).Errors);

    [Fact]
    public void TheUsageListsTheNewOptions()
    {
        foreach (var option in new[] { "--stop-running", "--uninstall-cleanup", "--keep-changes", "--changes-dump" })
            Assert.Contains(option, CommandLineOptions.Usage);
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

        Assert.Equal(Program.ExitStillRunning, exit);
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

        Assert.Equal(Program.ExitNotAllPutBack, exit);
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

    /// <summary>A fake Undo all: every change the selection takes, done when <paramref name="works"/> says so.</summary>
    private static IReadOnlyList<(ActiveChange Change, UndoResult Result)> Undo(Func<ActiveChange, bool>? which, Func<ActiveChange, bool> works) =>
        All.Where(c => which?.Invoke(c) ?? true)
            .Select(c => (c, works(c) ? new UndoResult(true, "put back") : new UndoResult(false, "PoolWatch could not reach the service manager.")))
            .ToList();

    private sealed class Release(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
