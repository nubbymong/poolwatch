using PoolWatch.Core.Findings;

namespace PoolWatch.Engine;

/// <summary>How this process was started, for the decisions that depend on it (set once by Program.Main).</summary>
internal static class RunFlags
{
    /// <summary>--allow-any-location: running elevated from a folder that is not admin-only (CI, development).</summary>
    public static bool AllowAnyLocation { get; set; }
    /// <summary>--ui-smoke: the CI UI test.</summary>
    public static bool UiSmoke { get; set; }
}

/// <summary>
/// S4 (review round 3): the "start at logon" task runs PoolWatch with highest privileges and no UAC prompt, so the exe it
/// starts must sit in an admin-only folder: otherwise any program the user runs can replace that file and is started as
/// administrator at the next logon (an earlier build let the task start a copy of PoolWatch on the Desktop).
/// Pure: the task as read, the current exe and the admin-only roots are the caller's.
/// </summary>
internal static class LogonTaskPolicy
{
    /// <summary>Null when PoolWatch may register (or re-point) the logon task to <paramref name="exePath"/>; else why not.</summary>
    public static string? WhyNotRegister(string? exePath, bool allowAnyLocation, bool uiSmoke, IEnumerable<string> adminRoots)
    {
        if (uiSmoke) return "the CI UI test never creates a logon task";
        if (allowAnyLocation) return "this PoolWatch runs with --allow-any-location, from a folder that is not administrator-only; such a location is never started with highest privileges at logon";
        if (exePath is null) return "PoolWatch's own path is not known";
        if (!InstallGuard.IsUnderAdminRoot(exePath, adminRoots))
            return $"PoolWatch runs from {exePath}, a folder you can write to without administrator rights; install it to " +
                   $"{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\\PoolWatch and start it from there first";
        return null;
    }

    /// <summary>
    /// One action of the task: an Exec action's program and arguments, or another kind of action (a COM handler, an
    /// e-mail, a message) that PoolWatch cannot check (<paramref name="IsExec"/> false; <paramref name="Command"/> says what it is).
    /// </summary>
    public sealed record TaskAction(string Command, string? Arguments, bool IsExec = true);

    /// <summary>The existing logon task, as read from Task Scheduler: every one of its actions (L2) and its run level.</summary>
    public sealed record ExistingTask(IReadOnlyList<TaskAction> Actions, bool Highest)
    {
        /// <summary>A task with one Exec action.</summary>
        public ExistingTask(string Command, string? Arguments, bool Highest) : this([new TaskAction(Command, Arguments)], Highest) { }
    }

    public enum Judgement { NoTask, Fine, Untrusted }

    /// <param name="Program">The program the task starts (environment variables expanded, quotes removed); for an untrusted
    /// task, its first action that is not safe.</param>
    /// <param name="CanRePoint">The running exe may take its place (it passed the same check).</param>
    public sealed record Verdict(Judgement Judgement, string? Program, string? Message, bool CanRePoint);

    /// <summary>
    /// Is the existing task safe? L2: Task Scheduler runs EVERY action with the task's privileges, so the task is untrusted
    /// when any action starts something outside the admin-only install roots (or a relative path), is not a program
    /// PoolWatch can check (a COM handler can load code registered by the user), or when there is no action at all.
    /// </summary>
    public static Verdict Judge(ExistingTask? task, string? currentExe, bool currentExeTrusted, IEnumerable<string> adminRoots, Func<string, string>? expand = null)
    {
        if (task is null) return new(Judgement.NoTask, null, null, false);
        var roots = adminRoots.ToList();
        IReadOnlyList<TaskAction> actions = task.Actions.Count > 0 ? task.Actions : [new TaskAction("", null)];
        string? first = null;
        foreach (var action in actions)
        {
            if (!action.IsExec) return Untrusted(task, action, action.Command, full: false, currentExe, currentExeTrusted);
            string program = (expand ?? Environment.ExpandEnvironmentVariables)(action.Command.Trim().Trim('"'));
            bool full = program.Length > 0 && Path.IsPathFullyQualified(program);
            if (!full || !InstallGuard.IsUnderAdminRoot(program, roots)) return Untrusted(task, action, program, full, currentExe, currentExeTrusted);
            first ??= program;
        }
        return new(Judgement.Fine, first, null, false);
    }

    private static Verdict Untrusted(ExistingTask task, TaskAction action, string program, bool full, string? currentExe, bool currentExeTrusted)
    {
        // Re-pointing replaces the whole task (every action) with one action that starts the running, installed exe.
        bool canRePoint = currentExeTrusted && currentExe is not null &&
                          (task.Actions.Count != 1 || !string.Equals(Path.GetFullPath(currentExe), full ? Path.GetFullPath(program) : program, StringComparison.OrdinalIgnoreCase));
        string highest = task.Highest ? " with highest privileges" : "";
        string message = action.IsExec
            ? $"Your PoolWatch logon task starts {(program.Length == 0 ? "(no program)" : program)}{highest} at every logon, from a folder you can " +
              "write to without administrator rights: a program you run could replace that file and be started as administrator at your next logon."
            : $"Your PoolWatch logon task runs {program}{highest} at every logon. PoolWatch cannot check what such an action loads, and it may be " +
              "changeable without administrator rights.";
        return new(Judgement.Untrusted, program, message, canRePoint);
    }
}

/// <summary>Reading and changing the logon task (tests pass a fake: no test touches Task Scheduler).</summary>
internal interface ILogonTasks
{
    LogonTaskPolicy.ExistingTask? Read();
    /// <summary>Registers (or replaces) the task to start the running exe; the reason when it did not.</summary>
    string? RegisterCurrent();
    string? Remove();
}

/// <summary>
/// On every start (review round 3, item 2): inspect the existing PoolWatch logon task; when it starts an exe outside the
/// admin-only install roots, say so (log and a Setup finding) and, with the owner's confirmation, point it at the running,
/// verified exe or remove it. Headless (no one to ask): said, nothing changed.
/// </summary>
internal sealed class LogonTaskCheck(ILogonTasks tasks, Func<LogonTaskPolicy.Verdict, LogonTaskCheck.Answer> ask, Action<string> log, Action<Finding> finding)
{
    public enum Answer { Leave, RePoint, Remove }

    public LogonTaskPolicy.Verdict Run(string? currentExe, bool currentExeTrusted, IEnumerable<string> adminRoots)
    {
        var v = LogonTaskPolicy.Judge(tasks.Read(), currentExe, currentExeTrusted, adminRoots);
        if (v.Judgement != LogonTaskPolicy.Judgement.Untrusted) return v;
        log("logon task: " + v.Message);
        var answer = ask(v);
        string outcome;
        if (answer == Answer.RePoint && v.CanRePoint)
            outcome = tasks.RegisterCurrent() is { } err ? $"it could not be pointed at {currentExe}: {err}" : $"you pointed it at {currentExe}";
        else if (answer == Answer.Remove)
            outcome = tasks.Remove() is { } err ? $"it could not be removed: {err}" : "you removed it";
        else outcome = "it was left as it is (PoolWatch asks again at the next start)";
        log("logon task: " + outcome);
        finding(new Finding
        {
            TimeUtc = DateTime.UtcNow, Kind = FindingKind.Setup, Severity = outcome.StartsWith("you ", StringComparison.Ordinal) ? FindingSeverity.Info : FindingSeverity.Warning,
            Title = $"{v.Message} {char.ToUpperInvariant(outcome[0])}{outcome[1..]}.",
        });
        return v;
    }
}
