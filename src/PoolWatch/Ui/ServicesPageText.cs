using System.Globalization;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Ui;

/// <summary>
/// The Services tab's words and the rules of its buttons, and the Tasks and startup apps tab's one call that makes a
/// change (all unit-tested): the status line, when Put back has nothing to do, the questions and the message after a
/// change, what "Turn off" says and when it and the Put back items can be pressed, and the switch of a startup app or
/// scheduled task (<see cref="Switch"/>, which records the switch and then makes it). While service-changes.json cannot
/// be read (<see cref="ServicesController.RecordError"/>), the tab's list of changes is not the whole record, so it never
/// says "nothing to put back": the status line says so in short, and Put back goes to the controller, which refuses with
/// the whole reason while the file still cannot be read.
/// </summary>
internal static class ServicesPageText
{
    /// <summary>The status line while PoolWatch's record of its service changes cannot be read; null when it was read.</summary>
    public static string? RecordProblem(string? recordError) => recordError is null ? null
        : "PoolWatch cannot read how it changed the services, so it cannot put them back yet (Put all back says why).";

    /// <summary>
    /// Why Put back (all: <paramref name="service"/> null, or one) stops before it asks the controller, or null to ask it:
    /// only while PoolWatch's record was read and holds no change of it.
    /// </summary>
    public static string? StopsBeforeRestore(string? service, ServicesController services)
    {
        if (services.RecordError is not null) return null; // the controller says why
        bool recorded = services.Changes.Any(c => service is null || c.Service.Equals(service, StringComparison.OrdinalIgnoreCase));
        return recorded ? null : NothingToRestoreText(service);
    }

    /// <summary>
    /// The ticked services "Turn off" changes, and how many are off already (<see cref="ServicePlan.OffAlready"/>): those
    /// are left as they are and not recorded, so its question lists only the others.
    /// </summary>
    public static (List<string> Change, int OffAlready) ApplyPlan(IReadOnlyList<string> ticked, IReadOnlyList<ServiceOption> options)
    {
        var byName = new Dictionary<string, ServiceOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in options) byName.TryAdd(o.Service.Name, o);
        var change = ticked.Where(n => !(byName.TryGetValue(n, out var o) && ServicePlan.OffAlready(o))).ToList();
        return (change, ticked.Count - change.Count);
    }

    /// <summary>
    /// Of the services "Turn off" changes, those it sets to Manual (<see cref="ServicePlan.OffStart"/>): it does not stop
    /// them, so its question says so apart from the ones it stops and disables.
    /// </summary>
    public static List<string> ToManual(IReadOnlyList<string> change, IReadOnlyList<ServiceOption> options)
    {
        var byName = new Dictionary<string, ServiceOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in options) byName.TryAdd(o.Service.Name, o);
        return change.Where(n => byName.TryGetValue(n, out var o) && ServicePlan.OffStart(o) == ServiceStartType.Manual).ToList();
    }

    /// <summary>
    /// "Turn off"'s question: "2 services will be stopped and set to not start: ... Continue?", with the one exception
    /// (<see cref="NotStoppedWhenDependedOn"/>); the services it sets to Manual (<paramref name="toManual"/>) apart, as it
    /// does not stop them; then how many ticked services are off already and left as they are.
    /// </summary>
    public static string ApplyQuestion(IReadOnlyList<string> change, int offAlready = 0, IReadOnlyCollection<string>? toManual = null)
    {
        var manual = toManual?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var stopped = change.Where(n => !manual.Contains(n)).ToList();
        var setManual = change.Where(manual.Contains).ToList();
        var text = new System.Text.StringBuilder();
        if (stopped.Count > 0)
            text.Append($"{stopped.Count} service{(stopped.Count == 1 ? "" : "s")} will be stopped and set to not start:\n\n  {string.Join("\n  ", stopped)}\n\n" +
                        $"{NotStoppedWhenDependedOn(stopped.Count == 1)}\n\n");
        if (setManual.Count > 0)
        {
            bool one = setManual.Count == 1;
            text.Append($"{setManual.Count} service{(one ? "" : "s")} will be set to Manual, so {(one ? "it starts" : "they start")} only when something asks for " +
                        $"{(one ? "it" : "them")} (PoolWatch does not stop {(one ? "it" : "them")}):\n\n  {string.Join("\n  ", setManual)}\n\n");
        }
        text.Append(offAlready <= 0 ? "" : offAlready == 1 ? "1 more ticked service is off already and is left as it is.\n\n"
            : $"{offAlready} more ticked services are off already and are left as they are.\n\n");
        return text.Append(SavedFirst).ToString();
    }

    /// <summary>
    /// The exception to "will be stopped" in Turn off's question, for one service (<paramref name="one"/>) or several:
    /// Windows does not stop a service while one that depends on it runs, and PoolWatch never stops a service you did not tick.
    /// </summary>
    public static string NotStoppedWhenDependedOn(bool one) => one
        ? "If a running service you did not tick depends on it, Windows does not stop it: it is set to not start and stops when Windows restarts."
        : "If a running service you did not tick depends on one of them, Windows does not stop that one: it is set to not start and stops when " +
          "Windows restarts.";

    /// <summary>The end of every question before a service change.</summary>
    public const string SavedFirst = "PoolWatch saves how each one was set first, so you can put it back. Continue?";

    /// <summary>"Turn off" when every ticked service is off already: nothing to change.</summary>
    public static string AllOffAlready(int ticked) => ticked == 1
        ? "The ticked service is off already, so there is nothing to turn off."
        : "The ticked services are all off already, so there is nothing to turn off.";

    /// <summary>The status line when the services could not be read (it never stays on "Reading services...").</summary>
    public static string ReadFailed(string error) => $"PoolWatch could not read the services: {error.TrimEnd('.')}. Refresh tries again.";

    /// <summary>"Nothing to put back", for every service (null) or one.</summary>
    public static string NothingToRestoreText(string? service) => service is null
        ? "Nothing to put back: PoolWatch has not changed any service."
        : $"PoolWatch has not changed {service}: nothing to put back.";

    /// <summary>
    /// The message after Turn off, Put back or Set to its Windows default: services with the same result on one line,
    /// their names first; a result about no one service ("(services)": the record could not be read; "(plan)") as its
    /// reason alone; <paramref name="none"/> when there is no result at all.
    /// </summary>
    public static string ResultMessage(IReadOnlyList<(string service, string? error)> results, string none)
    {
        if (results.Count == 0) return none;
        var lines = new List<string>();
        foreach (var same in results.GroupBy(r => r.error))
        {
            string names = string.Join(", ", same.Select(r => r.service).Where(s => !AboutNoItem(s)));
            lines.Add(same.Key is null ? (names.Length == 0 ? "Done." : $"{names}: done") : names.Length == 0 ? same.Key : $"{names}: {same.Key}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Whether a result is about no one item ("(plan)", "(services)", the AI agent's "(test run)"): a name in brackets. The
    /// one rule, for the Services tab and the AI agent's review alike: <see cref="ResultMessage"/> shows such a result
    /// without a name.
    /// </summary>
    internal static bool AboutNoItem(string name) => name.StartsWith('(') && name.EndsWith(')');

    // ---------------------------------------------------------------- the buttons

    /// <summary>
    /// The primary button's words: "Turn off 1 service", "Turn off 3 services"; "Turn off" when nothing ticked would
    /// change (<paramref name="change"/>: the ticked services not off already, <see cref="ApplyPlan"/>).
    /// </summary>
    public static string TurnOffText(int change) => change <= 0 ? "Turn off"
        : $"Turn off {change.ToString(CultureInfo.InvariantCulture)} service{(change == 1 ? "" : "s")}";

    /// <summary>
    /// When the Services tab's buttons can be pressed. <paramref name="busy"/>: the list is being read or a change is
    /// running. <paramref name="change"/>: how many ticked services Turn off would change. <paramref name="changed"/>:
    /// how many PoolWatch changed (Put all back), <paramref name="recordError"/>: that record cannot be read (Put back then
    /// goes to the controller, which says why). <paramref name="selected"/>: the first selected service, if any.
    /// </summary>
    public static ServiceButtons Buttons(bool busy, int change, int changed, string? recordError, ServiceOption? selected)
    {
        bool record = recordError is not null;
        bool one = selected is not null && (selected.PoolWatchChange is not null || record);
        bool toDefault = selected is { CanRestoreDefault: true };
        bool all = changed > 0 || record;
        return new ServiceButtons(TurnOffText(change), !busy && change > 0, !busy && (one || toDefault || all), !busy && one, !busy && toDefault, !busy && all, !busy);
    }

    // ---------------------------------------------------------------- the Tasks and startup apps tab's switch

    /// <summary>
    /// The Tasks and startup apps tab's switch of the selected scheduled task or startup app (<paramref name="selected"/>,
    /// the row's entry), recorded for Undo before it is made (<see cref="ChangeRecords.Toggle"/>), so it is an Active
    /// changes row; <paramref name="setTask"/> and <paramref name="setStartup"/> make the switch. Microsoft's own startup
    /// apps are refused, as the engine refuses Microsoft's own tasks. Null, or why it was not switched.
    /// </summary>
    /// <param name="via">Where the switch was made, as Active changes says it: this tab (<see cref="Via"/>), or an AI agent's answer.</param>
    public static string? Switch(object? selected, bool enable, ChangeRecords records,
        Func<string, bool, string?> setTask, Func<string, string, bool, string?> setStartup, string via = Via) => selected switch
    {
        ScheduledTaskView t => records.Toggle(ChangeKind.ScheduledTask, t.Path, t.Name, enable, via, () => setTask(t.Path, enable)),
        StartupView { Microsoft: true } => MicrosoftLeftAlone,
        StartupView su => records.Toggle(ChangeKind.StartupEntry, su.Location, su.Name, enable, via, () => setStartup(su.Location, su.Name, enable)),
        _ => "select a scheduled task or startup app",
    };

    /// <summary>Why a Microsoft startup app is not switched here.</summary>
    public const string MicrosoftLeftAlone = "PoolWatch leaves Microsoft's own startup apps alone";

    /// <summary>Where a switch was made, as Active changes says it.</summary>
    public const string Via = "the Tasks and startup apps tab";
}

/// <summary>
/// The Services tab's buttons as <see cref="ServicesPageText.Buttons"/> decides them: the primary button's words and
/// whether it is enabled, whether "Put back..." opens and which of its three items can be picked, and whether Refresh
/// can be pressed.
/// </summary>
internal sealed record ServiceButtons(string TurnOffText, bool TurnOff, bool PutBack, bool PutBackSelected, bool ToWindowsDefault, bool PutAllBack, bool Refresh);
