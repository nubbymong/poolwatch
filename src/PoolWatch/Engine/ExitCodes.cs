namespace PoolWatch.Engine;

/// <summary>
/// Every exit code PoolWatch's program (app\PoolWatch.exe) and its launcher (the install folder's PoolWatch.exe) end
/// with, each defined here once and each value used by one code only. This one source file is compiled into the program
/// and into the launcher, which passes the program's code on as it is. The setup and the uninstaller
/// (installer/PoolWatch.iss) name each code their steps can get with the same value, and docs/TECHNICAL.md lists them
/// all in one table; a test holds the three together.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Done: what was asked was done.</summary>
    internal const int Done = 0;
    /// <summary>
    /// An error stopped the step (PoolWatch says so on the console, in its log and in the tray app's box), so it may have
    /// done only part of what was asked.
    /// </summary>
    internal const int Failed = 1;
    /// <summary>The command line holds an option PoolWatch does not know, or one without the value it needs: nothing was done.</summary>
    internal const int BadCommandLine = 2;
    /// <summary>Another PoolWatch holds the one-instance mutex (the tray app and a headless run only).</summary>
    internal const int AlreadyRunning = 3;
    /// <summary>A clear task of your own is still there (you kept it, or a note says why), so the automatic clear stays off.</summary>
    internal const int ClearTaskKept = 4;
    /// <summary>
    /// PoolWatch runs as administrator and found that someone who is not an administrator could change its program, its
    /// launcher or a folder they are in, so it did nothing.
    /// </summary>
    internal const int NotInAdminFolder = 5;
    /// <summary>
    /// PoolWatch could not use its data folder, nor a folder of its own in its place (<see cref="DataRootRefusedException"/>):
    /// nothing was written to a data folder.
    /// </summary>
    internal const int DataNotSafe = 6;
    /// <summary>--stop-running and --uninstall-cleanup: a PoolWatch still runs, so nothing was changed.</summary>
    internal const int StillRunning = 7;
    /// <summary>
    /// --uninstall-cleanup: something could not be put back (it stays listed in the data folder, so a later PoolWatch can
    /// still put it back); --service-restore: a service was not put back, or PoolWatch holds no change of the one named.
    /// </summary>
    internal const int NotAllPutBack = 8;
    /// <summary>
    /// PoolWatch's private names (<see cref="AdminNamespace"/>) could not be opened, or its one-instance mutex could not be
    /// taken, so PoolWatch did not start, or could not ask a running PoolWatch to exit: nothing was done.
    /// </summary>
    internal const int NamesNotSafe = 9;
    /// <summary>
    /// PoolWatch did not run: its launcher could not start it, or PoolWatch, started on its own, could not start its
    /// launcher (which may be a declined administrator prompt).
    /// </summary>
    internal const int NotStarted = 10;
    /// <summary>
    /// PoolWatch refused this start: started by its launcher without administrator rights, or as administrator with .NET's
    /// start-up variables in its environment.
    /// </summary>
    internal const int Refused = 11;
    /// <summary>
    /// --uninstall-cleanup ran (as <see cref="Done"/> or <see cref="NotAllPutBack"/> would say), but on the folder PoolWatch
    /// keeps its data in while it could not use the usual one, so the uninstaller reads and deletes nothing in the usual one.
    /// </summary>
    internal const int OtherDataFolder = 12;
    /// <summary>
    /// --service-off: the service was not turned off (refused, not changed, or set to Disabled but not known to have
    /// stopped), or the services could not be read.
    /// </summary>
    internal const int NotAllTurnedOff = 13;
    /// <summary>
    /// The token-leak guard's hidden helper could not write its result file; the PoolWatch that started it reads no result
    /// then.
    /// </summary>
    internal const int ResultNotWritten = 14;
    /// <summary>--selftest: one of its checks failed (selftest.json says which).</summary>
    internal const int ChecksFailed = 15;
}
