using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Services.Agent;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Ui;

/// <summary>What the Services tab's two AI-agent buttons open (<see cref="ServicesTabView.UseAgent"/>).</summary>
internal interface IAgentWindows
{
    /// <summary>"Decide with your AI agent": the window with the prompt to save or copy.</summary>
    void OpenPrompt();

    /// <summary>"Import answer": an Open dialog, then the review of what the answer would change.</summary>
    void ImportAnswer();
}

/// <summary>Runs work off the UI thread and its result back on it, while the window is still there (the tests run it in line).</summary>
internal interface IAgentWork
{
    void Run<T>(Control window, Func<T> work, Action<T> done, Action<Exception> failed);
}

/// <summary>
/// The real one: a worker thread, then the window's own thread; nothing when the window has gone meanwhile. A failure of
/// the work is always observed, the window there or not.
/// </summary>
internal sealed class BackgroundWork : IAgentWork
{
    public static readonly BackgroundWork Instance = new();

    public void Run<T>(Control window, Func<T> work, Action<T> done, Action<Exception> failed) => Start(window, work, done, failed);

    /// <summary><see cref="Run{T}"/>; the task that ends once the result is handed to the window's thread, or dropped (the tests wait on it).</summary>
    internal static Task Start<T>(Control window, Func<T> work, Action<T> done, Action<Exception> failed) =>
        Task.Run(work).ContinueWith(t =>
        {
            Exception? error = t.IsFaulted ? t.Exception!.GetBaseException() : null;
            if (!window.IsHandleCreated || window.IsDisposed) return;
            try
            {
                window.BeginInvoke(() =>
                {
                    if (window.IsDisposed) return;
                    if (error is not null) failed(error);
                    else done(t.Result);
                });
            }
            catch (InvalidOperationException) { } // the window is closing
        }, TaskScheduler.Default);
}

/// <summary>
/// The Services tab's two AI-agent buttons at work. "Decide with your AI agent" opens <see cref="AgentPromptForm"/>, which
/// reads this PC in the background and shows the whole prompt; it is written only where the user picks in a Save dialog.
/// "Import answer" asks for the answer file in an Open dialog, then opens <see cref="AgentReviewForm"/>, which reads the
/// file (bounded, never a link), builds the list from this PC again and reads the answer against it, and shows exactly
/// what Apply would change and what it refuses. Nothing changes until Apply, and then only through the paths that
/// record each change first (<see cref="AgentApply"/>), so every change is under Active changes with a way to put it
/// back. In the CI UI test, Apply changes nothing. Each window is created when opened and disposed when closed. Once the
/// review closes after an Apply ran, <paramref name="applied"/> is told (the main window has the Tasks and startup apps tab
/// read its list again, as Apply may have switched some of them).
/// </summary>
/// <param name="applied">Told on the UI thread once the review closed after an Apply ran; null: nothing to tell.</param>
internal sealed class AgentDecisions(Form owner, SetupContext context, IAgentFiles? files = null, Action? applied = null) : IAgentWindows
{
    private readonly IAgentFiles _files = files ?? new WindowsAgentFiles();
    private readonly TasksAndStartup _tasks = new(context.Engine.Log);

    private ServicesController Services => context.Analysis.Diagnostics.Services;

    public void OpenPrompt()
    {
        var read = Reader();
        using var form = new AgentPromptForm(() => AgentPrompt.Build(read.Items(), read.Windows()), _files);
        form.FitTo(Screen.FromControl(owner).WorkingArea);
        form.ShowDialog(owner);
    }

    public void ImportAnswer()
    {
        if (_files.AskOpenPath(owner) is not { } path) return;
        var read = Reader();
        bool ran = false;
        using var form = new AgentReviewForm(() => AgentReview.Load(path, _files, read.Items), (plan, services) =>
        {
            ran = true; // set on the worker before it changes anything; read below once the window has closed
            return Apply(plan, services);
        }, _files);
        form.FitTo(Screen.FromControl(owner).WorkingArea);
        form.ShowDialog(owner);
        if (Volatile.Read(ref ran)) applied?.Invoke();
    }

    /// <summary>
    /// What reads this PC for the list, made on the UI thread: the live processes and what each costs now, as the Tasks and
    /// startup apps tab has them, are taken here; the services, startup apps and tasks are read when the window asks.
    /// </summary>
    private AgentPcReader Reader()
    {
        var live = context.Engine.Registry.LiveRecords().Select(r => new LiveImage(r.Key, r.Image, r.ImagePath)).ToList();
        var cost = ProcessLoad.CostNow(context.Analysis.Diagnostics.Load);
        var system = context.Analysis.Host;
        return new AgentPcReader(
            () => AgentPcReader.Build(Services.Options(), _tasks.Startup(), _tasks.Tasks(), live, cost, ServicesController.InsiderEnrolled(),
                MainForm.CurrentRedaction(), Environment.ExpandEnvironmentVariables),
            () =>
            {
                var s = system.GetSystemInfo();
                return AgentPrompt.WindowsText(s.OsDescription, s.OsBuild, ServicesController.InsiderEnrolled());
            });
    }

    /// <summary>Apply, off the UI thread, after the review and its questions: nothing in the CI UI test.</summary>
    private List<(string Name, string? Error)> Apply(AgentPlan plan, IReadOnlyCollection<string> services) =>
        AgentApply.UnlessTestRun(context.Smoke,
            () => AgentApply.Run(plan, services, Services, context.Ledger.Records, _tasks, (what, name) => context.Analysis.Diagnostics.RecordOwnerChange(what, name)));
}

/// <summary>Reads this PC for the AI-agent list and the prompt's Windows line (each throws as the Core's builders do).</summary>
internal sealed class AgentPcReader(Func<AgentItemList> items, Func<string> windows)
{
    public AgentItemList Items() => items();

    public string Windows() => windows();

    /// <summary>
    /// The list for this PC: the services as the Services tab has them, and the startup apps and scheduled tasks as the
    /// Tasks and startup apps tab reads them (<see cref="TasksTab.Entries"/>), each with who it is from as that tab says it
    /// and what its program costs now, matched as that tab matches it. A startup app keeps its name and place exactly as
    /// Windows has them, as its switch needs them; what goes in the prompt is redacted and bounded by
    /// <see cref="AgentItemList.Build"/>, and never a command line.
    /// </summary>
    public static AgentItemList Build(IReadOnlyList<ServiceOption> options, IEnumerable<StartupView> startup, IEnumerable<ScheduledTaskView> tasks,
        IReadOnlyList<LiveImage> live, Func<ProcessKey, (double cores, long faultsPerSec)?> cost, bool insiderEnrolled, RedactionInfo redaction,
        Func<string, string> expand)
    {
        EntryCostResult? CostOf(TaskEntry e) => e.Exe is null || EntryCost.ViaLauncher(e.Exe) ? null : EntryCost.Match(e.Exe, live, cost);
        var apps = new List<AgentStartupEntry>();
        var jobs = new List<AgentScheduledTask>();
        foreach (var e in TasksTab.Entries(tasks, startup, expand))
        {
            if (e.Source is StartupView su) apps.Add(new AgentStartupEntry(su.Name, su.Location, e.Publisher, su.Enabled, su.Microsoft, CostOf(e)));
            else if (e.Source is ScheduledTaskView t) jobs.Add(new AgentScheduledTask(t.Path, t.Name, e.Publisher, t.Enabled, CostOf(e)));
        }
        return AgentItemList.Build(options, apps, jobs, insiderEnrolled, redaction);
    }
}

/// <summary>
/// Apply for an imported answer, after the user reviewed it: only what the plan holds (each item matched on this PC when
/// the answer was read, never a name straight from the file), through the paths the tabs use, each of which records the
/// change before it makes it. The services go through <see cref="ServicesController.Apply"/> (its gate again against the
/// live services, the original recorded first, one change at a time); a startup app or a scheduled task through the
/// Tasks and startup apps tab's own switch (<see cref="ServicesPageText.Switch"/>: recorded first with
/// <see cref="ChangeRecords.Toggle"/>, Microsoft's own startup apps refused there, Microsoft's own tasks and an entry no
/// longer there refused by the engine). Every change says it came from <see cref="AgentAnswer.Via"/>, so Active changes
/// lists it and can put it back. One item that fails stops none of the others: when the services' gate refuses the whole
/// list (a service uninstalled since the review was read, say), the services it refuses are found against one more read
/// of the services and left as they are, each named with why, and the rest are turned off.
/// </summary>
internal static class AgentApply
{
    /// <summary>The one result <see cref="ServicesController.Apply"/> gives when its gate refuses the whole list: nothing was changed.</summary>
    internal const string PlanRefused = "(plan)";

    /// <summary>The one result when the services could not be turned off at all (the services' Apply failed): nothing was changed.</summary>
    internal const string ServicesFailed = "(services)";

    /// <summary>The one result in the CI UI test, where nothing is changed.</summary>
    internal const string TestRun = "(test run)";

    /// <summary>Apply in the app: <paramref name="run"/>, except in the CI UI test, where nothing is changed and it says so.</summary>
    public static List<(string Name, string? Error)> UnlessTestRun(bool smoke, Func<List<(string Name, string? Error)>> run) =>
        smoke ? [(TestRun, RecommendationActions.Refused)] : run();

    /// <param name="services">The plan's services to turn off, by their names on this PC (the user may have left out those PoolWatch does not recognise).</param>
    /// <param name="timeline">Told of each startup app or task switched (its line and its name), as the tab tells the timeline.</param>
    public static List<(string Name, string? Error)> Run(AgentPlan plan, IReadOnlyCollection<string> services, ServicesController controller, ChangeRecords records,
        ITaskToggles toggles, Action<string, string?>? timeline = null)
    {
        var results = new List<(string Name, string? Error)>();
        var wanted = services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = plan.Services.Select(s => s.Option.Service.Name).Where(wanted.Contains).ToList();
        if (names.Count > 0) results.AddRange(TurnOff(controller, names));
        foreach (var s in plan.Startup)
        {
            var e = s.Entry;
            var view = new StartupView(e.Name, "", e.Location, Enabled: true, e.Microsoft);
            results.Add((s.Item.Name, Off(view, e.Name, records, toggles, timeline)));
        }
        foreach (var t in plan.Tasks)
        {
            var job = t.Task;
            var view = new ScheduledTaskView(job.Path, job.Name, job.Author ?? "", Enabled: true, Runs: "", NextRun: "");
            results.Add((t.Item.Name, Off(view, job.Name, records, toggles, timeline)));
        }
        return results;
    }

    /// <summary>
    /// The services through the Services tab's Apply, all in one go. Its gate refuses the whole list when one of them no
    /// longer passes (it changes nothing then, and says so in one result, <see cref="PlanRefused"/>). Then the services the
    /// gate refuses on their own are found by the controller against one more read of the services, as its gate reads them
    /// (<see cref="ServicesController.RefusedAlone"/>), and named with why, and the rest go through Apply again, in one go,
    /// so its gate decides each of them (all of them again when none is refused on its own by then). A failure that is not
    /// the gate's (the services cannot be read) is said once, never tried again for each service.
    /// </summary>
    private static List<(string Name, string? Error)> TurnOff(ServicesController controller, List<string> names)
    {
        var all = Apply(controller, names);
        if (names.Count == 1 || all is not [(PlanRefused, _)]) return Named(all, names);
        List<(string Name, string? Error)> refused;
        try { refused = controller.RefusedAlone(names, AgentAnswer.Via).Select(r => (r.service, (string?)r.error)).ToList(); }
        catch (Exception) { refused = []; } // then Apply's gate says why again, for the whole list
        var aside = refused.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rest = names.Where(n => !aside.Contains(n)).ToList();
        var results = rest.Count > 0 ? Named(Apply(controller, rest), rest) : [];
        results.AddRange(refused);
        return results;
    }

    private static List<(string Name, string? Error)> Apply(ServicesController controller, List<string> names)
    {
        try { return controller.Apply(names, AgentAnswer.Via); }
        catch (Exception ex) { return [(ServicesFailed, ex.Message)]; }
    }

    /// <summary>A result about no one service (<see cref="ServicesPageText.AboutNoItem"/>) for a single service is that service's.</summary>
    private static List<(string Name, string? Error)> Named(List<(string Name, string? Error)> results, List<string> names) =>
        names.Count == 1 ? results.Select(r => ServicesPageText.AboutNoItem(r.Name) ? (names[0], r.Error) : r).ToList() : results;

    /// <summary>
    /// One startup app or task switched off with the Tasks and startup apps tab's switch, recorded first as the answer's;
    /// the timeline told as that tab tells it. Null, or why it was not switched.
    /// </summary>
    private static string? Off(object view, string name, ChangeRecords records, ITaskToggles toggles, Action<string, string?>? timeline)
    {
        string? error;
        try { error = ServicesPageText.Switch(view, enable: false, records, toggles.SetTaskEnabled, toggles.SetStartupEnabled, AgentAnswer.Via); }
        catch (Exception ex) { error = ex.Message; }
        if (error is null) timeline?.Invoke(TaskActions.Timeline(new ToggleTarget(view, Enable: false)), name);
        return error;
    }
}
