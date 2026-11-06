using System.Globalization;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>What a row of the Tasks and startup apps tab is.</summary>
internal enum TaskKind { StartupApp, ScheduledTask, Busy }

/// <summary>The tab's four views: everything, or one kind ("Busy in the background": the apps that have been heavy for 30 minutes).</summary>
internal enum TaskSegment { All, StartupApps, ScheduledTasks, Busy }

/// <summary>
/// A startup app, a scheduled task or an app busy in the background, as the tab lists it. <see cref="Source"/> is what
/// the switch acts on (a <see cref="ScheduledTaskView"/> or <see cref="StartupView"/>; null for a busy app, which has
/// none). <see cref="Exe"/> is the program it runs, environment variables expanded.
/// </summary>
internal sealed record TaskEntry(string Key, TaskKind Kind, string Name, string? Publisher, string Starts, string Command, bool? Enabled, bool Microsoft,
    object? Source, string? Exe);

/// <summary>
/// What a row's program costs now. <see cref="Running"/>: a process of it runs (always for a busy app). Not
/// <see cref="Measurable"/>: it runs through a shared launcher (cmd, PowerShell...), so it cannot be told apart.
/// <see cref="EndKeys"/>: the processes "End it now" may end, those matched by their own full path (a busy app's own
/// process); empty when there are none. <see cref="Average"/>: the cost is a busy app's average over 30 minutes. Not
/// <see cref="Measured"/>: it runs, but PoolWatch has no sampling window yet to say what it costs ("measuring", never "idle").
/// </summary>
internal sealed record TaskCost(bool Running, bool Measurable, double Cores, long PageFaultsPerSec, int Processes, IReadOnlyList<ProcessKey> EndKeys, bool Average = false,
    bool Measured = true)
{
    public static readonly TaskCost NotRunning = new(false, true, 0, 0, 0, []);
    public static readonly TaskCost NotMeasurable = new(false, false, 0, 0, 0, []);

    /// <summary>The level of what it costs now; null when it is not running, cannot be measured or is not measured yet.</summary>
    public ImpactLevel? Level => Running && Measured ? EntryCost.Level(Cores, PageFaultsPerSec) : null;

    /// <summary>
    /// What the ranking sorts by: its cores, a little for page faults; -1 when it is not running or cannot be measured,
    /// -0.5 while it runs but is not measured yet (above what does not run, below what is idle).
    /// </summary>
    public double Rank => !Running ? -1 : !Measured ? -0.5 : Cores + Math.Min(PageFaultsPerSec, 10_000_000) / 10_000_000.0;
}

/// <summary>A row: the entry and what it costs now; <paramref name="Heavy"/>: its program has been heavy in the background for 30 minutes.</summary>
internal sealed record TaskRow(TaskEntry Entry, TaskCost Cost, bool Heavy = false)
{
    public string Key => Entry.Key;
}

/// <summary>
/// What the tab shows: one segment, the rows whose words hold <see cref="Search"/>, of those the ones not running only
/// with <see cref="ShowNotRunning"/> and Microsoft's own only with <see cref="ShowMicrosoft"/>. Both are off at first. A
/// row whose program cannot be measured shows (it may be running).
/// </summary>
internal sealed record TaskFilter(TaskSegment Segment = TaskSegment.All, string Search = "", bool ShowNotRunning = false, bool ShowMicrosoft = false)
{
    public static readonly TaskFilter Default = new();

    public bool Shows(TaskRow r)
    {
        bool inSegment = Segment switch
        {
            TaskSegment.StartupApps => r.Entry.Kind == TaskKind.StartupApp,
            TaskSegment.ScheduledTasks => r.Entry.Kind == TaskKind.ScheduledTask,
            TaskSegment.Busy => r.Entry.Kind == TaskKind.Busy || r.Heavy,
            _ => true,
        };
        if (!inSegment) return false;
        if (r.Entry.Microsoft && !ShowMicrosoft) return false;
        if (!ShowNotRunning && !r.Cost.Running && r.Cost.Measurable) return false;
        string s = Search.Trim();
        if (s.Length == 0) return true;
        bool In(string? t) => t is { Length: > 0 } && t.Contains(s, StringComparison.OrdinalIgnoreCase);
        return In(r.Entry.Name) || In(r.Entry.Publisher) || In(r.Entry.Command) || In(TaskRows.KindText(r.Entry.Kind));
    }
}

/// <summary>
/// The detail panel under the list for the selected row: its title and the line beside it (who it is from, when it
/// starts), what it costs now, what the two actions do (the switch is reversible, ending it is not), the full command,
/// and the buttons: the switch's words (null: none for this row), whether "End it now" and "Open file location" can be
/// pressed.
/// </summary>
internal sealed record TaskDetail(string Title, string Sub, string CostLine, string Explain, string Command, string? Toggle, bool CanEnd, bool CanOpen);

/// <summary>
/// The Tasks and startup apps tab's rows (pure, so the order and the words are tested): ranked by what they cost now,
/// most first, by default; any column sorts on a click, a second click reversing it; ties by name.
/// </summary>
internal static class TaskRows
{
    public static readonly string[] Columns = ["Name", "Kind", "Cost now", "Starts"];
    public const int NameColumn = 0, KindColumn = 1, CostColumn = 2, StartsColumn = 3;

    public const string SignIn = "at sign-in";

    public static string KindText(TaskKind kind) => kind switch
    {
        TaskKind.StartupApp => "Startup app",
        TaskKind.ScheduledTask => "Scheduled task",
        _ => "Busy in the background",
    };

    /// <summary>The words of a segment's button.</summary>
    public static string SegmentText(TaskSegment s) => s switch
    {
        TaskSegment.StartupApps => "Startup apps",
        TaskSegment.ScheduledTasks => "Scheduled tasks",
        TaskSegment.Busy => "Busy in the background",
        _ => "All",
    };

    /// <summary>The Cost now cell: "Low: 0.2 core", "None: idle", "not running", "not measurable", "measuring…" (no meter).</summary>
    public static string CostText(TaskCost c) =>
        !c.Measurable ? "not measurable"
        : !c.Running ? "not running"
        : !c.Measured ? Measuring
        : $"{ImpactScale.Word(c.Level!.Value)}: {ServicePlan.Cost(c.Cores, c.PageFaultsPerSec)}";

    /// <summary>A cost cell before PoolWatch's first sampling window (the Services list says it too).</summary>
    public const string Measuring = "measuring…";

    /// <summary>The Starts cell: "at sign-in" for a startup app, a task's triggers, "running now" for a busy app.</summary>
    public static string StartsText(TaskEntry e) => e.Kind switch
    {
        TaskKind.StartupApp => SignIn,
        TaskKind.ScheduledTask => e.Starts.Length > 0 ? e.Starts : "on its schedule",
        _ => "running now",
    };

    public static string[] Cells(TaskRow r) => [r.Entry.Name, KindText(r.Entry.Kind), CostText(r.Cost), StartsText(r.Entry)];

    /// <summary>The rows the filter shows, sorted by <paramref name="column"/> (the Cost column by what each costs), ties by name.</summary>
    public static List<TaskRow> Build(IEnumerable<TaskRow> rows, TaskFilter filter, int column, bool ascending)
    {
        var shown = rows.Where(filter.Shows).ToList();
        shown.Sort((a, b) =>
        {
            int c = column == CostColumn ? a.Cost.Rank.CompareTo(b.Cost.Rank)
                : StringComparer.OrdinalIgnoreCase.Compare(Cells(a)[Math.Clamp(column, 0, Columns.Length - 1)], Cells(b)[Math.Clamp(column, 0, Columns.Length - 1)]);
            if (!ascending) c = -c;
            if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a.Entry.Name, b.Entry.Name);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return shown;
    }

    /// <summary>
    /// Joins the busy apps to the startup apps and tasks: a busy app whose process one of them runs (matched by full
    /// path) is that entry's row, marked <see cref="TaskRow.Heavy"/>, not a row of its own.
    /// </summary>
    public static List<TaskRow> Merge(IReadOnlyList<TaskRow> entries, IReadOnlyList<TaskRow> busy)
    {
        var byKey = new Dictionary<ProcessKey, int>();
        for (int i = 0; i < entries.Count; i++)
            foreach (var k in entries[i].Cost.EndKeys) byKey.TryAdd(k, i);
        var rows = entries.ToList();
        foreach (var b in busy)
        {
            if (b.Cost.EndKeys.Count > 0 && byKey.TryGetValue(b.Cost.EndKeys[0], out int i)) rows[i] = rows[i] with { Heavy = true };
            else rows.Add(b);
        }
        return rows;
    }

    /// <summary>
    /// "End it now" is offered: not Microsoft's own, running, and every process of it known for sure (matched by its own
    /// full path, or a busy app's own process). The detail panel's button and the action itself follow this one rule.
    /// </summary>
    public static bool CanEnd(TaskRow r) => !r.Entry.Microsoft && r.Cost.Running && r.Cost.EndKeys.Count > 0;

    /// <summary>
    /// What the list and the panel under it say while the list shows no row (null while it shows one): the list is still
    /// being read, the read failed, nothing matches the search, or nothing listed is running (the default view).
    /// </summary>
    public static (string Title, string Line)? Empty(TaskFilter filter, int shown, bool loaded, string? readError)
    {
        if (shown > 0) return null;
        string again = readError is null ? "" : $"{readError.Trim().TrimEnd('.')}. Refresh tries again.";
        if (!loaded) return readError is not null ? ("PoolWatch could not read the startup apps and scheduled tasks.", again) : ("Reading the startup apps and scheduled tasks…", "");
        string search = filter.Search.Trim();
        (string Title, string Line) why = search.Length > 0 ? ($"Nothing here matches \"{search}\".", filter.ShowNotRunning ? "" : "Tick Show not running to look through them all.")
            : filter.ShowNotRunning ? ("Nothing to list here.", "") : ("Nothing here is running now.", "Tick Show not running to see them all.");
        // A read that failed with the rows from before still listed: what the search or the filter left is said, and the failure.
        return readError is null ? why : (why.Title, string.Join(" ", new[] { why.Line, "The list could not be read again: " + again }.Where(s => s.Length > 0)));
    }

    /// <summary>The detail panel for a row.</summary>
    public static TaskDetail Detail(TaskRow r)
    {
        var e = r.Entry;
        string from = e.Publisher is { Length: > 0 } p ? $"by {p}, " : "";
        string sub = e.Kind switch
        {
            TaskKind.StartupApp => $"{from}starts at sign-in",
            TaskKind.ScheduledTask => $"{from}{(e.Starts.Length > 0 ? "runs " + e.Starts : "runs on its schedule")}",
            _ => $"{from}running in the background",
        };
        bool canEnd = CanEnd(r);
        string? toggle = e.Microsoft || e.Kind == TaskKind.Busy || e.Enabled is null ? null : (e.Kind, e.Enabled.Value) switch
        {
            (TaskKind.StartupApp, true) => "Don't start at sign-in",
            (TaskKind.StartupApp, false) => "Start at sign-in again",
            (_, true) => "Turn this task off",
            _ => "Turn this task back on",
        };
        string end = canEnd ? " \"End it now\" closes it until it next starts, and can't be undone."
            : r.Cost.Running && !e.Microsoft ? " PoolWatch can't tell for sure which running program is this one, so it does not offer to end it." : "";
        string explain = e.Microsoft ? "PoolWatch leaves Microsoft's own startup apps alone: Task Manager's Startup apps can switch them."
            : e.Kind == TaskKind.Busy ? (canEnd ? "\"End it now\" closes it until it next starts, and can't be undone. Unsaved work in it is lost." : "")
            : e.Kind == TaskKind.StartupApp
                ? (e.Enabled == false
                    ? "It is switched off: it does not start at sign-in. \"Start at sign-in again\" switches it back on, the same switch as Task Manager's Startup apps."
                    : "\"Don't start at sign-in\" is the same switch as Task Manager's Startup apps: nothing is deleted, and you can put it back.") + end
                : (e.Enabled == false
                    ? "It is switched off: it does not run. \"Turn this task back on\" lets it run again, the same switch as Enable in Task Scheduler."
                    : "\"Turn this task off\" is the same switch as Disable in Task Scheduler: nothing is deleted, and you can put it back.") + end;
        return new TaskDetail(e.Name, sub, CostLine(r), explain.Trim(), e.Command, toggle, canEnd, e.Exe is { } x && Path.IsPathFullyQualified(x));
    }

    /// <summary>"Using 0.2 of a core and 9,000 page faults a second right now (1 process).", "Idle right now (2 processes).", "Not running now."</summary>
    public static string CostLine(TaskRow r)
    {
        var c = r.Cost;
        if (!c.Measurable)
        {
            string launcher = r.Entry.Exe is { } x ? Path.GetFileName(x) : "a launcher";
            return $"PoolWatch cannot tell what it uses: it runs through {launcher}, and every other program that does would be counted too.";
        }
        if (!c.Running) return "Not running now.";
        string processes = c.Processes == 1 ? "1 process" : $"{c.Processes.ToString(CultureInfo.InvariantCulture)} processes";
        if (!c.Measured) return $"Running ({processes}). PoolWatch is still measuring what it uses.";
        var parts = new List<string>();
        if (c.Cores >= ProcessCostHistory.BusyCores)
            parts.Add(c.Cores < 1.05 ? $"{c.Cores.ToString("0.0", CultureInfo.InvariantCulture)} of a core" : $"{c.Cores.ToString("0.0", CultureInfo.InvariantCulture)} cores");
        if (c.PageFaultsPerSec >= ProcessCostHistory.BusyFaults) parts.Add($"{c.PageFaultsPerSec.ToString("N0", CultureInfo.InvariantCulture)} page faults a second");
        string when = c.Average ? "on average over the last 30 minutes, with no window in front" : "right now";
        return parts.Count == 0 ? $"Idle {when} ({processes})." : $"Using {string.Join(" and ", parts)} {when} ({processes}).";
    }
}
