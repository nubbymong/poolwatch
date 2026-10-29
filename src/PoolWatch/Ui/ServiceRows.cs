using System.Globalization;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Services;

namespace PoolWatch.Ui;

/// <summary>
/// One service on the Services list: its option, its cells as shown, and its colour. A class, not a value: the list
/// keeps each row and changes its Cost cell (<see cref="Cells"/>[<see cref="ServiceRows.CostColumn"/>]) and its
/// <see cref="Level"/> (the meter drawn before it) in place while the tab shows.
/// </summary>
internal sealed class ServiceRow(ServiceOption option, string[] cells, Color? fore)
{
    public ServiceOption Option { get; } = option;

    /// <summary>The cells as shown, in <see cref="ServiceRows.Columns"/> order; the Cost cell changes in place.</summary>
    public string[] Cells { get; } = cells;

    public Color? Fore { get; } = fore;

    public string Name => Option.Service.Name;
    public bool Tickable => Option.CanTurnOff;

    /// <summary>What the service's process costs now, for the Cost cell's meter; null draws no meter (not running, not measured yet).</summary>
    public ImpactLevel? Level { get; set; }

    /// <summary>
    /// What shows when the pointer rests on the row: the catalogue's note (or the service's own description), then the
    /// whole source line, which a narrow Source column cuts short (<see cref="ServiceRows.TipOf"/>).
    /// </summary>
    public string? Tip { get; init; }
}

/// <summary>
/// A group's header row on the Services list: its name and note, how many of its shown services are ticked, its own
/// tick (all, some or none of them) and whether it is folded away. The protected group's services are never changed: it
/// has no tick.
/// </summary>
internal sealed record ServiceGroupRow(string Id, string Name, string Note, bool Protected, int Count, int Tickable, int Ticked, bool Collapsed)
{
    public CheckState Tick => Ticked == 0 ? CheckState.Unchecked : Ticked >= Tickable ? CheckState.Checked : CheckState.Indeterminate;

    /// <summary>"1 of 2 ticked", or for the protected group "267 services, never changed".</summary>
    public string CountText => Protected
        ? $"{Count.ToString(CultureInfo.InvariantCulture)} service{(Count == 1 ? "" : "s")}, never changed"
        : $"{Ticked.ToString(CultureInfo.InvariantCulture)} of {Tickable.ToString(CultureInfo.InvariantCulture)} ticked";

    /// <summary>What a screen reader says for the row (its item text): the name, the note, the count and whether it is folded.</summary>
    public string AccessibleText => $"{Name}: {Note.TrimEnd('.')}. {CountText}, {(Collapsed ? "collapsed" : "expanded")}";

    public string Key => GroupKey(Id);

    public static string GroupKey(string id) => "group:" + id;
}

/// <summary>
/// What the Services list shows: the services whose name or words hold <see cref="Search"/>, and of those the ones not
/// running only with <see cref="ShowNotRunning"/> and the protected ones only with <see cref="ShowProtected"/>. Both
/// are off at first, so the list opens on what runs and may be turned off: what is off already (a service PoolWatch
/// turned off too: Active changes and "Put all back" still put it back) waits for "Show not running". A ticked service
/// always shows, so nothing hidden is turned off.
/// </summary>
internal sealed record ServiceFilter(string Search = "", bool ShowNotRunning = false, bool ShowProtected = false)
{
    public static readonly ServiceFilter Default = new();

    public bool Shows(ServiceRow row, bool ticked)
    {
        var o = row.Option;
        if (o.Group == ServiceGroup.Protected && !ShowProtected) return false;
        if (!ShowNotRunning && !o.Service.Running && !ticked) return false;
        return Matches(row, Search);
    }

    /// <summary>The search, ignoring case, in the row's cells, its service and display names, its catalogue name and its publisher.</summary>
    public static bool Matches(ServiceRow row, string search)
    {
        string s = search.Trim();
        if (s.Length == 0) return true;
        bool In(string? text) => text is { Length: > 0 } && text.Contains(s, StringComparison.OrdinalIgnoreCase);
        var o = row.Option;
        return row.Cells.Any(In) || In(o.Service.Name) || In(o.Service.DisplayName) || In(o.Catalogue?.Name) || In(o.Service.Publisher);
    }
}

/// <summary>
/// The Services list's rows (pure, so the order is tested): Windows services first, then the services of other apps,
/// then, when they are shown, the protected ones, each group under a header row and sorted by the clicked column, then by
/// name, a second click reversing both. A Windows service is one the catalogue knows Windows' own start type for; the
/// sources it cites were written for virtual desktops and company PCs, so the group says so once. Other apps have no
/// official source: the AI agent is the way to decide about them.
/// </summary>
internal static class ServiceRows
{
    public const string WindowsId = "windows", AppsId = "apps", ProtectedId = "protected";
    public const string WindowsName = "Windows services", AppsName = "From other apps";
    public const string ProtectedName = "Protected (security, Windows Update, input/audio, Hyper-V and WSL, core)";
    public const string AppsNote = "No official source: decide with your AI agent";
    public const string ProtectedNote = "PoolWatch never changes these";

    /// <summary>The groups in the order they show, each with its header's words.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Note)> Groups =
    [
        (WindowsId, WindowsName, ServiceSourceText.GroupCaveat),
        (AppsId, AppsName, AppsNote),
        (ProtectedId, ProtectedName, ProtectedNote),
    ];

    /// <summary>The columns, in order (the tick box is in the first).</summary>
    public static readonly string[] Columns = ["Service", "You lose", "Cost", "State", "Source"];

    public const int NameColumn = 0, LoseColumn = 1, CostColumn = 2, StateColumn = 3, SourceColumn = 4;

    /// <summary>A Windows service: one the catalogue knows Windows ships, with Windows' own start type for it.</summary>
    public static bool IsWindows(ServiceOption o) => o.Group != ServiceGroup.Protected && o.Catalogue?.WindowsDefaults is { Count: > 0 };

    /// <summary>The group a service is listed under: protected, a Windows service, or another app's.</summary>
    public static string GroupOf(ServiceOption o) =>
        o.Group == ServiceGroup.Protected ? ProtectedId : IsWindows(o) ? WindowsId : AppsId;

    /// <summary>"Xbox Live Auth Manager (XblAuthManager)": the name Windows shows, then the service's own name; the name alone when they are the same.</summary>
    public static string ServiceText(ServiceOption o) =>
        o.Service.DisplayName is { Length: > 0 } d && !d.Equals(o.Service.Name, StringComparison.OrdinalIgnoreCase) ? $"{d} ({o.Service.Name})" : o.Service.Name;

    /// <summary>What turning it off loses: the catalogue's words; for a protected service why it is never changed; else "not known".</summary>
    public static string LoseText(ServiceOption o) =>
        o.Catalogue is { } c ? c.Lose
        : o.Group == ServiceGroup.Protected ? $"never changed ({o.ProtectedReason ?? "core Windows service"})"
        : o.Service.Publisher is { Length: > 0 } p ? $"not known (signed by {p})" : "not known";

    /// <summary>
    /// Where the advice to turn it off comes from, saying what that source is written for
    /// (<see cref="ServiceSourceText.Label"/>): every Windows service's row, and another app's only when the catalogue
    /// gives it a source (its group already says it has none); nothing for a protected service.
    /// </summary>
    public static string SourceText(ServiceOption o) => GroupOf(o) switch
    {
        WindowsId => ServiceSourceText.Label(o.Catalogue!.Source.Kind),
        AppsId when o.Catalogue?.Source is { Kind: not ServiceSourceKind.None } s => ServiceSourceText.Label(s.Kind),
        _ => "",
    };

    /// <summary>The State column: <see cref="ServicePlan.StateText"/>, and the Windows default when "Set to its Windows default" is offered.</summary>
    public static string StateText(ServiceOption o) =>
        ServicePlan.StateText(o) + (o.CanRestoreDefault ? $"; Windows default: {o.WindowsDefault}" : "");

    /// <summary>A row's colour: grey when protected, dark gold to check first, else the list's.</summary>
    public static Color? ForeOf(ServiceOption o) =>
        o.Group == ServiceGroup.Protected ? Color.Gray : o.Catalogue is { Risk: ServiceRisk.CheckFirst } ? Color.DarkGoldenrod : null;

    /// <summary>A service's row as the tab shows it, with its Cost cell <paramref name="cost"/>.</summary>
    public static ServiceRow RowOf(ServiceOption o, (string Text, ImpactLevel? Level) cost) =>
        new(o, [ServiceText(o), LoseText(o), cost.Text, StateText(o), SourceText(o)], ForeOf(o)) { Level = cost.Level, Tip = TipOf(o) };

    /// <summary>
    /// A row's tooltip: the catalogue's note, else the service's own description, then on its own line the whole source
    /// line (<see cref="SourceText"/>), which the Source column cuts short when the window is narrow; null when there is
    /// neither.
    /// </summary>
    public static string? TipOf(ServiceOption o)
    {
        string? about = o.Catalogue?.Note is { Length: > 0 } note ? note : o.Service.Description is { Length: > 0 } d ? d : null;
        string source = SourceText(o);
        return about is null ? (source.Length > 0 ? source : null) : source.Length > 0 ? $"{about}\n{source}" : about;
    }

    /// <summary>
    /// The Cost cell of a service whose process costs its <see cref="ServiceView.Cores"/> and page faults now: "Low: 0.2
    /// core", "None: idle", with a shared host said; "not running" with no meter; "measuring…" with no meter while
    /// PoolWatch has no sampling window yet (not <paramref name="measured"/>), never "idle" then.
    /// </summary>
    public static (string Text, ImpactLevel? Level) CostOf(ServiceView s, bool measured = true)
    {
        if (!s.Running) return ("not running", null);
        if (s.ProcessId == 0) return ("", null);
        if (!measured) return (TaskRows.Measuring, null);
        var level = EntryCost.Level(s.Cores, s.PageFaultsPerSec);
        return ($"{ImpactScale.Word(level)}: {ServicePlan.CostText(s)}", level);
    }

    /// <summary>The rows sorted by <paramref name="column"/> (the Cost column by what it costs), then by name, reversed when not <paramref name="ascending"/>.</summary>
    public static IEnumerable<ServiceRow> Sorted(IEnumerable<ServiceRow> rows, int column, bool ascending)
    {
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            int c = column == CostColumn ? ListSort.CostKey(KeyOf(a, column)).CompareTo(ListSort.CostKey(KeyOf(b, column)))
                : StringComparer.OrdinalIgnoreCase.Compare(KeyOf(a, column), KeyOf(b, column));
            if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return ascending ? c : -c;
        });
        return list;
    }

    private static string KeyOf(ServiceRow r, int column) => column >= 0 && column < r.Cells.Length ? r.Cells[column] : r.Name;

    /// <summary>The rows <paramref name="filter"/> shows (<paramref name="ticked"/>: the ticked services, which a filter never hides).</summary>
    public static List<ServiceRow> Shown(IEnumerable<ServiceRow> rows, ServiceFilter filter, IReadOnlySet<string> ticked) =>
        rows.Where(r => filter.Shows(r, ticked.Contains(r.Name))).ToList();

    /// <summary>
    /// What the list shows, top to bottom: <see cref="ServiceGroupRow"/> and <see cref="ServiceRow"/> items. A header for
    /// each group that has a shown service, then its shown services unless it is folded; <paramref name="ticked"/> counts
    /// for each header.
    /// </summary>
    public static List<object> Build(IReadOnlyList<ServiceRow> rows, ServiceFilter filter, IReadOnlySet<string> collapsed, int column, bool ascending,
        IReadOnlySet<string> ticked)
    {
        var sorted = Sorted(Shown(rows, filter, ticked), column, ascending).ToList();
        var result = new List<object>();
        foreach (var (id, name, note) in Groups)
        {
            var g = sorted.Where(r => GroupOf(r.Option) == id).ToList();
            if (g.Count == 0) continue;
            int tickable = g.Count(r => r.Tickable);
            int on = g.Count(r => r.Tickable && ticked.Contains(r.Name));
            bool folded = collapsed.Contains(id);
            result.Add(new ServiceGroupRow(id, name, note, id == ProtectedId, g.Count, tickable, on, folded));
            if (!folded) result.AddRange(g);
        }
        return result;
    }

    /// <summary>"Showing 14 of 337 services".</summary>
    public static string ShownText(int shown, int total) =>
        $"Showing {shown.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} service{(total == 1 ? "" : "s")}";
}
