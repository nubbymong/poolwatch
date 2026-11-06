using PoolWatch.Core.Findings;
using PoolWatch.Core.Setup;

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
/// The "start at logon" task runs PoolWatch with highest privileges and no UAC prompt, so the exe it
/// starts must sit in an admin-only folder: otherwise any program the user runs can replace that file and is started as
/// administrator at the next logon (an earlier build let the task start a copy of PoolWatch on the Desktop). And it must
/// start PoolWatch's launcher (the install folder's PoolWatch.exe), which gives PoolWatch a clean environment, never
/// PoolWatch's program itself or an older PoolWatch that has no launcher.
/// Pure: the task as read, the current exe and the admin-only roots are the caller's.
/// </summary>
internal static class LogonTaskPolicy
{
    /// <summary>Null when PoolWatch may register (or re-point) the logon task to the launcher of <paramref name="exePath"/>; else why not.</summary>
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
    /// One action of the task: an Exec action's program, arguments and start folder, or another kind of action (a COM
    /// handler, an e-mail, a message) that PoolWatch cannot check (<paramref name="IsExec"/> false; <paramref
    /// name="Command"/> says what it is).
    /// </summary>
    public sealed record TaskAction(string Command, string? Arguments, bool IsExec = true, string? WorkingDirectory = null);

    /// <summary>The task's sign-in triggers (TASK_TRIGGER_LOGON): one of them is on; there are some, and each is turned off; there is none.</summary>
    public enum Trigger { On, Off, None }

    /// <summary>
    /// The existing logon task, as read from Task Scheduler: every one of its actions, whether it runs with highest
    /// privileges (<paramref name="Highest"/>), whether it is on (<paramref name="Enabled"/>: false when the owner turned it
    /// off in Task Scheduler), its sign-in triggers (<paramref name="SignInTrigger"/>), and the account and sign-in kind it
    /// runs with (<paramref name="UserId"/> as Task Scheduler gives it: a name or a SID; <paramref name="LogonType"/> a
    /// TASK_LOGON_* value). Each is null when Task Scheduler's answer for it could not be read; an account or a sign-in kind
    /// that is null is never PoolWatch's.
    /// </summary>
    public sealed record ExistingTask(IReadOnlyList<TaskAction> Actions, bool? Highest, bool? Enabled = true, Trigger? SignInTrigger = Trigger.On,
        string? UserId = null, int? LogonType = null)
    {
        /// <summary>A task with one Exec action.</summary>
        public ExistingTask(string Command, string? Arguments, bool? Highest, bool? Enabled = true, Trigger? SignInTrigger = Trigger.On,
            string? UserId = null, int? LogonType = null)
            : this([new TaskAction(Command, Arguments)], Highest, Enabled, SignInTrigger, UserId, LogonType) { }
    }

    /// <summary>The arguments PoolWatch gives the task (LogonTask.TaskXml): the tray app.</summary>
    public const string TaskArguments = "--tray";

    /// <summary>TASK_LOGON_INTERACTIVE_TOKEN: the task runs in your session when you sign in, as PoolWatch registers it.</summary>
    public const int InteractiveToken = 3;

    /// <summary>
    /// Pure: the <see cref="SignInState"/> of <paramref name="task"/>, judged <paramref name="judgement"/> (<see
    /// cref="Judge"/>). A switch of its own that keeps it from running comes first (it turned off; its sign-in triggers
    /// turned off; none), then what could not be read, then the check at start's judgement, then its run level: PoolWatch's
    /// launcher starts only as administrator, so where UAC gives a task without highest privileges a token without
    /// administrator rights (<paramref name="highestNeeded"/>, <see cref="Ui.LogonTask.HighestNeeded()"/>), such a task
    /// cannot start it.
    /// </summary>
    public static SignInState AtSignIn(ExistingTask? task, Judgement judgement, bool highestNeeded) =>
        task is null ? SignInState.NoTask
        : task.Enabled == false ? SignInState.TaskOff
        : task.SignInTrigger == Trigger.Off ? SignInState.TriggerOff
        : task.SignInTrigger == Trigger.None ? SignInState.NoTrigger
        : task.Enabled is null || task.SignInTrigger is null || (highestNeeded && task.Highest is null) ? SignInState.NotKnown
        : judgement != Judgement.Fine ? SignInState.NotSafe
        : highestNeeded && task.Highest == false ? SignInState.NotHighest
        : SignInState.Starts;

    /// <summary>
    /// Untrusted: an action starts something a standard user could change or that PoolWatch cannot check. Outdated: every
    /// action is in an admin-only folder, but one names a program with PoolWatch's name that is not a launcher (<see
    /// cref="LaunchGate.Judge"/>): a file that is not there, so that sign-in starts nothing; PoolWatch's program
    /// app\PoolWatch.exe itself, or an older PoolWatch, which has no launcher, so that sign-in would start PoolWatch with
    /// the environment Task Scheduler builds from the account, not the one its launcher builds; or a file of another
    /// version than the program below it.
    /// </summary>
    public enum Judgement { NoTask, Fine, Untrusted, Outdated }

    /// <param name="Program">The program the task starts (quotes removed); for an untrusted or outdated task, its first
    /// action that is not safe.</param>
    /// <param name="CanRePoint">The running PoolWatch may put its own launcher in its place (it passed the same check).</param>
    /// <param name="On">Whether the task is on (null when it could not be read): a re-point keeps it so.</param>
    /// <param name="SignInTrigger">The task's sign-in triggers (null when they could not be read): a re-point keeps them on or off.</param>
    public sealed record Verdict(Judgement Judgement, string? Program, string? Message, bool CanRePoint, bool? On = true, Trigger? SignInTrigger = Trigger.On);

    /// <summary>
    /// Is the existing task the one PoolWatch makes? Task Scheduler runs EVERY action with the task's privileges, so
    /// the task is untrusted when any action starts something outside the admin-only install roots (or a relative path),
    /// names its program through an environment variable (Task Scheduler fills it in from the account's environment, which
    /// a program running as the user can change), is not a program PoolWatch can check (a COM handler can load code
    /// registered by the user), or when there is no action at all. A task that is otherwise fine but names a program with
    /// PoolWatch's name that is not a launcher (<see cref="LaunchGate.Judge"/>, by default on the real files) is outdated.
    /// Then the rest of the definition, which a program without administrator rights could have chosen too: exactly one
    /// action; its program, when the running PoolWatch is the installed, checked one (<paramref
    /// name="currentExeTrusted"/>), this PoolWatch's own launcher (either of its names in this install folder); the
    /// arguments PoolWatch gives (<see cref="TaskArguments"/>) and no start folder; and the principal PoolWatch registers:
    /// this account (<paramref name="isThisAccount"/>), signed in interactively, with highest privileges (unless
    /// <paramref name="judgeRunLevel"/> is false: <see cref="AtSignIn"/> judges the run level itself, with this PC's need
    /// for it). Anything else is untrusted, and the running PoolWatch, when it is installed and checked, may replace the
    /// whole task with PoolWatch's own.
    /// </summary>
    /// <param name="isThisAccount">Whether the task's account (a name or a SID, or null when it could not be read) is the
    /// account this PoolWatch runs as.</param>
    public static Verdict Judge(ExistingTask? task, string? currentExe, bool currentExeTrusted, IEnumerable<string> adminRoots,
        Func<string?, bool> isThisAccount, LayoutFiles? files = null, bool judgeRunLevel = true)
    {
        if (task is null) return new(Judgement.NoTask, null, null, false);
        files ??= LayoutFiles.OnDisk;
        var roots = adminRoots.ToList();
        IReadOnlyList<TaskAction> actions = task.Actions.Count > 0 ? task.Actions : [new TaskAction("", null)];
        string? first = null, outdated = null;
        var state = LaunchGate.LauncherState.Launcher;
        foreach (var action in actions)
        {
            if (!action.IsExec) return Untrusted(task, action.Command, Why.NotAProgram, currentExe, currentExeTrusted);
            string program = action.Command.Trim().Trim('"');
            if (program.Contains('%')) return Untrusted(task, program, Why.Variable, currentExe, currentExeTrusted);
            if (program.Length == 0 || !Path.IsPathFullyQualified(program) || !InstallGuard.IsUnderAdminRoot(program, roots))
                return Untrusted(task, program, Why.Writable, currentExe, currentExeTrusted);
            first ??= program;
            if (outdated is null && LaunchGate.IsPoolWatchName(program) && LaunchGate.Judge(program, files) is not LaunchGate.LauncherState.Launcher and var s)
            {
                outdated = program;
                state = s;
            }
        }
        if (outdated is not null)
            return new(Judgement.Outdated, outdated, OutdatedMessage(outdated, state, task.Highest == true), currentExeTrusted && currentExe is not null, task.Enabled, task.SignInTrigger);
        var only = actions[0];
        string literal = only.Command.Trim().Trim('"');
        string? differs =
            actions.Count != 1 ? $"it has {actions.Count} actions, where PoolWatch's has one" :
            currentExeTrusted && currentExe is not null && !IsThisLauncher(literal, currentExe)
                ? $"it starts {literal} rather than this PoolWatch's launcher ({LaunchGate.ExpectedLauncher(currentExe)})" :
            !string.Equals((only.Arguments ?? "").Trim(), TaskArguments, StringComparison.Ordinal)
                ? $"it starts PoolWatch with \"{(only.Arguments ?? "").Trim()}\" rather than {TaskArguments}" :
            !string.IsNullOrWhiteSpace(only.WorkingDirectory) ? $"it starts PoolWatch in {only.WorkingDirectory.Trim()}" :
            !isThisAccount(task.UserId) ? $"it runs as {(string.IsNullOrEmpty(task.UserId) ? "an account PoolWatch could not read" : task.UserId)}, not as you" :
            task.LogonType != InteractiveToken ? "it does not run in your session when you sign in" :
            judgeRunLevel && task.Highest is null ? "PoolWatch could not read whether it runs with highest privileges" :
            judgeRunLevel && task.Highest == false ? "it does not run with highest privileges" : null;
        if (differs is null) return new(Judgement.Fine, first, null, false);
        return new(Judgement.Untrusted, literal,
            $"Your PoolWatch logon task is not the one PoolWatch makes: {differs}. Only PoolWatch's own task should start it at " +
            "every logon; this one could start PoolWatch in a way you did not choose.", CanRePoint(currentExe, currentExeTrusted), task.Enabled, task.SignInTrigger);
    }

    /// <summary>Whether <paramref name="program"/> is the launcher of the PoolWatch at <paramref name="currentExe"/>, under either of its names in its install folder.</summary>
    private static bool IsThisLauncher(string program, string currentExe) =>
        LaunchGate.LaunchersOf(currentExe).Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(program), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the task's account (<paramref name="userId"/>, as Task Scheduler gives it: a name or a SID; null when it
    /// could not be read) is the account this PoolWatch runs as (<paramref name="myName"/>, DOMAIN\name as Windows gives
    /// it, and <paramref name="mySid"/>): the same text as either, whatever the case, or the same SID once looked up
    /// (<paramref name="sameAccount"/>). The text alone is enough, so a name that cannot be looked up now (a domain
    /// account while the PC is offline) still matches the account PoolWatch registers the task for.
    /// </summary>
    public static bool IsAccount(string? userId, string? myName, string? mySid, Func<string, string, bool> sameAccount)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        string id = userId.Trim();
        if (myName is { Length: > 0 } && id.Equals(myName, StringComparison.OrdinalIgnoreCase)) return true;
        if (mySid is not { Length: > 0 }) return false;
        if (id.Equals(mySid, StringComparison.OrdinalIgnoreCase)) return true;
        try { return sameAccount(id, mySid); }
        catch (Exception) { return false; }
    }

    /// <summary>Replacing the task gives it PoolWatch's own definition (every action, the account), for the running, installed PoolWatch.</summary>
    private static bool CanRePoint(string? currentExe, bool currentExeTrusted) => currentExeTrusted && currentExe is not null;

    /// <summary>Pure: what is said about a task whose <paramref name="program"/>, with PoolWatch's name, is not a launcher (<paramref name="state"/>).</summary>
    internal static string OutdatedMessage(string program, LaunchGate.LauncherState state, bool highest)
    {
        string start = $"Your PoolWatch logon task starts {program}{(highest ? " with highest privileges" : "")}, ";
        const string environment = "at sign-in PoolWatch would get your account's environment as it is, not the clean one its launcher gives it.";
        return start + state switch
        {
            LaunchGate.LauncherState.NotThere => "which is not there, so PoolWatch does not start when you sign in.",
            // Which of the two is older cannot be told from here: only an older program left under the launcher's name
            // starts without the launcher's environment.
            LaunchGate.LauncherState.OtherVersion =>
                $"which is not of the same version as the PoolWatch below it ({Path.Combine(Path.GetDirectoryName(program) ?? "", LaunchGate.AppFolderName, LaunchGate.ProgramFileName)}). " +
                "If it is an older PoolWatch without a launcher, left there when a newer copy was unpacked over it, " + environment,
            _ => "which is not PoolWatch's launcher (it is PoolWatch's program itself, or an older PoolWatch without a launcher), so " + environment,
        };
    }

    private enum Why { Writable, Variable, NotAProgram }

    private static Verdict Untrusted(ExistingTask task, string program, Why why, string? currentExe, bool currentExeTrusted)
    {
        // Re-pointing replaces the whole task (every action) with one action that starts the launcher of the running,
        // installed PoolWatch.
        bool canRePoint = currentExeTrusted && currentExe is not null &&
                          (task.Actions.Count != 1 || !string.Equals(LaunchGate.ExpectedLauncher(currentExe), program, StringComparison.OrdinalIgnoreCase));
        string highest = task.Highest == true ? " with highest privileges" : "";
        string shown = program.Length == 0 ? "(no program)" : program;
        string message = why switch
        {
            Why.NotAProgram => $"Your PoolWatch logon task runs {program}{highest} at every logon. PoolWatch cannot check what such an action loads, and it may be " +
                               "changeable without administrator rights.",
            Why.Variable => $"Your PoolWatch logon task starts {shown}{highest} at every logon, a program named through an environment variable: Task Scheduler " +
                            "fills it in from your account's environment, which a program you run can change without administrator rights, and so choose " +
                            "what is started as administrator at your next logon.",
            _ => $"Your PoolWatch logon task starts {shown}{highest} at every logon, from a folder you can write to without administrator rights: a " +
                 "program you run could replace that file and be started as administrator at your next logon.",
        };
        return new(Judgement.Untrusted, program, message, canRePoint, task.Enabled, task.SignInTrigger);
    }
}

/// <summary>Reading and changing the logon task (tests pass a fake: no test touches Task Scheduler).</summary>
internal interface ILogonTasks
{
    /// <summary>The task, or null when there is none; throws when it is there but could not be read.</summary>
    LogonTaskPolicy.ExistingTask? Read();
    /// <summary>
    /// Registers (or replaces) the task to start the running PoolWatch's launcher, for the account that runs it, on or (with
    /// <paramref name="on"/> false) turned off, with its sign-in trigger on or (with <paramref name="signInTrigger"/> false)
    /// turned off; the reason when it did not.
    /// </summary>
    string? RegisterCurrent(bool on = true, bool signInTrigger = true);
    string? Remove();
    /// <summary>Whether the task's account (a name or a SID; null when it could not be read) is the one this PoolWatch runs as.</summary>
    bool IsThisAccount(string? userId);
}

/// <summary>
/// On every start: inspect the existing PoolWatch logon task; when it is not the one PoolWatch
/// makes (<see cref="LogonTaskPolicy.Judge"/>: it starts an exe outside the admin-only install roots, a program with
/// PoolWatch's name that is not its launcher, or anything PoolWatch cannot check, or it differs from PoolWatch's own in
/// any other part), or when it cannot be read, say so (log and a Setup finding) and, with the owner's confirmation,
/// replace it with PoolWatch's own task for the running PoolWatch's launcher, or remove it. Headless (no one to ask):
/// said, nothing changed.
/// </summary>
internal sealed class LogonTaskCheck(ILogonTasks tasks, Func<LogonTaskPolicy.Verdict, LogonTaskCheck.Answer> ask, Action<string> log, Action<Finding> finding,
    LayoutFiles? files = null)
{
    public enum Answer { Leave, RePoint, Remove }

    /// <summary>
    /// Pure: what the question adds for a re-point, which keeps the task and its sign-in trigger on or off as they are
    /// (<paramref name="on"/>, <paramref name="signInTrigger"/>: the verdict's). A task with no sign-in trigger gets
    /// PoolWatch's, turned off, as its only trigger, so it still does not run when you sign in; one whose state could not
    /// be read is registered on. Nothing when both are on.
    /// </summary>
    public static string RePointKeeps(bool? on, LogonTaskPolicy.Trigger? signInTrigger)
    {
        var (off, unknown) = Sorted(on, signInTrigger, "the task", "its sign-in trigger");
        bool none = signInTrigger == LogonTaskPolicy.Trigger.None;
        string text = "";
        if (off.Count > 0) text += $"; {string.Join(" and ", off)} {(off.Count == 1 ? "stays off, as it is" : "stay off, as they are")} now";
        if (none) text += "; it has no sign-in trigger now, and gets PoolWatch's, turned off, as its only trigger";
        if (off.Count > 0 || none) text += $", so PoolWatch does not start when you sign in ({SignInStates.TickInSettings}, when you want it to)";
        if (unknown.Count > 0)
            text += $"; {string.Join(" and ", unknown)} {(unknown.Count == 1 ? "is turned on, as whether it was" : "are turned on, as whether they were")} on could not be read";
        return text;
    }

    /// <summary>
    /// Pure: what the outcome of a re-point adds: what stayed off, the sign-in trigger turned off that a task with none got,
    /// and what was turned on because whether it was on could not be read (nothing when the task and its trigger were on).
    /// </summary>
    public static string RePointKept(bool? on, LogonTaskPolicy.Trigger? signInTrigger)
    {
        var (off, unknown) = Sorted(on, signInTrigger, "it", "its sign-in trigger");
        string text = off switch
        {
            [var one] => $"; {one} stays off, as it was",
            [var a, var b] => $"; {a} and {b} stay off, as they were",
            _ => "",
        };
        if (signInTrigger == LogonTaskPolicy.Trigger.None) text += "; it has PoolWatch's sign-in trigger, turned off, as it had none";
        return text + unknown switch
        {
            [var one] => $"; {one} is turned on, as whether it was on could not be read",
            [var a, var b] => $"; {a} and {b} are turned on, as whether they were on could not be read",
            _ => "",
        };
    }

    /// <summary>The names of the two that are off, and of those whose state could not be read (a task with no sign-in trigger is in neither).</summary>
    private static (List<string> off, List<string> unknown) Sorted(bool? on, LogonTaskPolicy.Trigger? signInTrigger, string task, string trigger)
    {
        var (off, unknown) = (new List<string>(), new List<string>());
        if (on == false) off.Add(task);
        else if (on is null) unknown.Add(task);
        if (signInTrigger == LogonTaskPolicy.Trigger.Off) off.Add(trigger);
        else if (signInTrigger is null) unknown.Add(trigger);
        return (off, unknown);
    }

    public LogonTaskPolicy.Verdict Run(string? currentExe, bool currentExeTrusted, IEnumerable<string> adminRoots)
    {
        LogonTaskPolicy.Verdict v;
        try { v = LogonTaskPolicy.Judge(tasks.Read(), currentExe, currentExeTrusted, adminRoots, tasks.IsThisAccount, files); }
        catch (Exception ex)
        {
            // A task that is there but cannot be read cannot be judged: it is asked about as one that is not safe, and
            // nothing is done to it unless the owner says so.
            v = new(LogonTaskPolicy.Judgement.Untrusted, null,
                $"Your PoolWatch logon task could not be read ({ex.Message}), so PoolWatch cannot tell what it starts at every logon.",
                currentExeTrusted && currentExe is not null, On: null, SignInTrigger: null);
        }
        if (v.Judgement is not (LogonTaskPolicy.Judgement.Untrusted or LogonTaskPolicy.Judgement.Outdated)) return v;
        log("logon task: " + v.Message);
        var answer = ask(v);
        string outcome;
        string target = currentExe is null ? LaunchGate.LauncherFileNames[0] : LaunchGate.LauncherOf(currentExe, files ?? LayoutFiles.OnDisk) ?? LaunchGate.ExpectedLauncher(currentExe);
        // Replacing it with PoolWatch's own task keeps the task and its sign-in trigger on or off as they were, each on when
        // that could not be read, and gives a task with no sign-in trigger PoolWatch's, turned off (the question says so:
        // RePointKeeps).
        if (answer == Answer.RePoint && v.CanRePoint)
            outcome = tasks.RegisterCurrent(on: v.On != false, signInTrigger: v.SignInTrigger is not (LogonTaskPolicy.Trigger.Off or LogonTaskPolicy.Trigger.None)) is { } err
                ? $"it could not be replaced with PoolWatch's own task, which starts {target}: {err}"
                : $"you replaced it with PoolWatch's own task, which starts {target}{RePointKept(v.On, v.SignInTrigger)}";
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
