using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>What "End it now" would end for a row: the processes, or why it ends nothing (<see cref="Refusal"/>).</summary>
internal sealed record EndPlan(IReadOnlyList<ProcessKey> Keys, string? Refusal);

/// <summary>What the switch of a row acts on (a <see cref="ScheduledTaskView"/> or <see cref="StartupView"/>) and which way.</summary>
internal sealed record ToggleTarget(object Source, bool Enable);

/// <summary>
/// The decisions behind the Tasks and startup apps tab's two actions, pure so they are tested without a window or a
/// process: which processes "End it now" may end and how it asks, how it ends them (each through a handle checked
/// against its start time, <see cref="ProcessEnder"/>), and what the switch acts on and which way.
/// </summary>
internal static class TaskActions
{
    public const string SelectRunning = "Select a running app first.";

    /// <summary>
    /// "End it now" for <paramref name="row"/>: only what <see cref="TaskRows.CanEnd"/> offers, and only when no process
    /// of it is one PoolWatch never ends (<see cref="EndProcessPolicy"/>: a core or security process, one that hosts a
    /// Windows service, a VM's memory): then nothing of it is ended. <paramref name="imageOf"/>: a process's image as
    /// PoolWatch knows it (null: not known, the program's own file name is used); <paramref name="hostedServices"/>: the
    /// services a PID hosts.
    /// </summary>
    public static EndPlan End(TaskRow? row, Func<ProcessKey, string?> imageOf, Func<int, IReadOnlyList<string>> hostedServices)
    {
        if (row is null || !TaskRows.CanEnd(row)) return new([], SelectRunning);
        foreach (var key in row.Cost.EndKeys)
        {
            string image = row.Entry.Source is BusyApp b && b.Key == key ? b.Image : imageOf(key) ?? Path.GetFileName(row.Entry.Exe ?? "");
            if (EndProcessPolicy.WhyNot(key.Pid, image, hostedServices(key.Pid)) is { } why)
                return new([], $"PoolWatch will not end {row.Entry.Name}: {why}.");
        }
        return new(row.Cost.EndKeys, null);
    }

    /// <summary>The one question before "End it now": what it is, its processes (five at most, then how many more), its program and command.</summary>
    public static string EndQuestion(TaskRow row, IReadOnlyList<ProcessKey> keys)
    {
        static string Line(ProcessKey k) => $"PID {k.Pid}" + (k.CreateTime > 0 ? $", started {Fmt.Clock(DateTime.FromFileTimeUtc(k.CreateTime))}" : "");
        string pids = string.Join("\n", keys.Take(5).Select(Line)) + (keys.Count > 5 ? $"\nand {keys.Count - 5} more" : "");
        string detail = $"{row.Entry.Name}\n{pids}\n{row.Entry.Exe ?? ""}" + (row.Entry.Source is BusyApp { CommandLine: { } cl } ? $"\n{cl}" : "");
        return $"End it now?\n\n{detail}\n\nIt closes until it next starts, and this can't be undone. Unsaved work in it is lost. PoolWatch never uninstalls anything.";
    }

    /// <summary>
    /// Ends the processes of <paramref name="plan"/>, each through <see cref="ProcessEnder"/> (opened first, its start
    /// time compared, ended through that handle). <paramref name="ask"/> is asked once, at the first process; a "no" ends
    /// nothing more. <paramref name="ended"/> is told of each process ended. The problems, one line each.
    /// </summary>
    public static List<string> RunEnd(EndPlan plan, string identity, IProcessOpener opener, Func<bool> ask, Action<ProcessKey> ended)
    {
        var problems = new List<string>();
        if (plan.Refusal is not null) return problems;
        bool? answer = null;
        bool Ask() => answer ??= ask();
        foreach (var key in plan.Keys)
        {
            var r = ProcessEnder.End(opener, key, identity, Ask);
            if (r.Outcome == ProcessEnder.Outcome.Cancelled) break;
            if (r.Outcome == ProcessEnder.Outcome.Ended) ended(key);
            else problems.Add(r.Message);
        }
        return problems;
    }

    /// <summary>
    /// The switch of <paramref name="row"/>: the task or startup app it acts on and the other way from how it is now;
    /// null for a row with no switch (a busy app, Microsoft's own, a state not read).
    /// </summary>
    public static ToggleTarget? Toggle(TaskRow? row) =>
        row?.Entry is { Microsoft: false, Enabled: { } enabled, Source: ScheduledTaskView or StartupView } e ? new ToggleTarget(e.Source!, !enabled) : null;

    /// <summary>The line the timeline keeps for a switch made, as the service changes have theirs.</summary>
    public static string Timeline(ToggleTarget t) => t.Source switch
    {
        ScheduledTaskView task => $"Scheduled task {(t.Enable ? "enabled" : "disabled")} by you: {task.Path}",
        StartupView su => $"Startup app {(t.Enable ? "set to start at sign-in" : "set not to start at sign-in")} by you: {su.Location}\\{su.Name} (Task Manager shows the same switch)",
        _ => "",
    };
}
