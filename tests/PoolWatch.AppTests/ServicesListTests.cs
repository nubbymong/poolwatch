using System.Drawing.Imaging;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Services;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>A small PC's services as the Services tab lists them (no real service is read).</summary>
internal static class SomeServices
{
    public static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    private static ServiceView V(string name, ServiceStartType start, bool running = false, bool ms = true) =>
        new() { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = ms };

    /// <summary>
    /// Windows services (the catalogue knows Windows' own start type): DiagTrack and WSearch run, the others do not;
    /// another app's: AdobeARMservice (in the catalogue) and SomeVendorSvc (not in it) run; WinDefend and RpcSs are protected.
    /// </summary>
    public static List<ServiceOption> Options(IReadOnlyCollection<ServiceChange>? changes = null) => ServicePlan.Options(
    [
        V("DiagTrack", ServiceStartType.Automatic, running: true), V("InventorySvc", ServiceStartType.Disabled), V("PcaSvc", ServiceStartType.Automatic),
        V("SysMain", ServiceStartType.Automatic), V("WSearch", ServiceStartType.Automatic, running: true),
        V("Fax", ServiceStartType.Manual), V("edgeupdatem", ServiceStartType.Manual), V("edgeupdate", ServiceStartType.Automatic),
        V("SomeVendorSvc", ServiceStartType.Automatic, running: true, ms: false),
        V("WinDefend", ServiceStartType.Automatic, running: true), V("RpcSs", ServiceStartType.Automatic, running: true),
        V("AdobeARMservice", ServiceStartType.Automatic, running: true, ms: false),
    ], Cat, insiderEnrolled: false, new HashSet<string>(), changes);

    private static readonly Dictionary<string, double> Cores = new() { ["DiagTrack"] = 0.3, ["WSearch"] = 1.2, ["SomeVendorSvc"] = 0.6 };

    /// <summary>The Cost cell as the tab makes it, from a few made-up costs.</summary>
    public static (string, ImpactLevel?) Cost(ServiceOption o) => Cores.TryGetValue(o.Service.Name, out var c)
        ? ServiceRows.CostOf(o.Service with { Cores = c, ProcessId = 100 })
        : ServiceRows.CostOf(o.Service with { ProcessId = o.Service.Running ? 100 : 0 });

    /// <summary>The rows as the tab makes them.</summary>
    public static List<ServiceRow> Rows(IReadOnlyCollection<ServiceChange>? changes = null) => Options(changes).Select(o => ServiceRows.RowOf(o, Cost(o))).ToList();
}

/// <summary>
/// The Services list's rows: Windows services, then other apps', then (only when shown) the protected ones, each under a
/// header with its own tick and count; the two filters ("Show not running", "Show protected") off at first; the search;
/// sortable columns (a second click reverses). Pure ordering, no window.
/// </summary>
public class ServiceRowsTests
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

    private static List<string> Shown(List<object> rows) =>
        rows.Select(r => r is ServiceGroupRow g ? "# " + g.Name : ((ServiceRow)r).Name).ToList();

    private static List<string> Build(ServiceFilter filter, IReadOnlySet<string>? ticked = null, IReadOnlyCollection<ServiceChange>? changes = null, int column = 0, bool ascending = true) =>
        Shown(ServiceRows.Build(SomeServices.Rows(changes), filter, None, column, ascending, ticked ?? None));

    [Fact]
    public void AtFirstTheListShowsWhatRunsAndMayBeTurnedOffInTwoGroups()
    {
        Assert.Equal(new ServiceFilter("", ShowNotRunning: false, ShowProtected: false), ServiceFilter.Default);
        Assert.Equal(["# Windows services", "DiagTrack", "WSearch", "# From other apps", "AdobeARMservice", "SomeVendorSvc"], Build(ServiceFilter.Default));
    }

    [Fact]
    public void ShowNotRunningAddsTheServicesThatAreOffOrStopped()
    {
        Assert.Equal(["# Windows services", "DiagTrack", "edgeupdate", "edgeupdatem", "Fax", "InventorySvc", "PcaSvc", "SysMain", "WSearch",
            "# From other apps", "AdobeARMservice", "SomeVendorSvc"], Build(new ServiceFilter(ShowNotRunning: true)));
    }

    [Fact]
    public void ShowProtectedAddsTheProtectedGroupLastWithNoTick()
    {
        var rows = ServiceRows.Build(SomeServices.Rows(), new ServiceFilter(ShowProtected: true), None, 0, true, None);
        Assert.Equal(["# Windows services", "DiagTrack", "WSearch", "# From other apps", "AdobeARMservice", "SomeVendorSvc",
            "# " + ServiceRows.ProtectedName, "RpcSs", "WinDefend"], Shown(rows));
        var prot = rows.OfType<ServiceGroupRow>().Last();
        Assert.Equal((true, 0, "2 services, never changed"), (prot.Protected, prot.Tickable, prot.CountText));
    }

    [Fact]
    public void ATickedServiceShowsEvenWhenNotRunningAndOnePoolWatchTurnedOffWaitsForShowNotRunning()
    {
        // SysMain is ticked (a profile picked it): it shows, so nothing hidden is turned off. Fax is off because PoolWatch
        // turned it off: it is off already, so it waits for "Show not running" like every other service that is off
        // (Active changes and Put all back still put it back).
        var fax = new ServiceChange { Service = "Fax", OriginalStart = ServiceStartType.Manual, OriginalRunning = false, NewStart = ServiceStartType.Disabled, ChangedUtc = DateTime.UtcNow, Via = "manual" };
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SysMain" };
        Assert.Equal(["# Windows services", "DiagTrack", "SysMain", "WSearch", "# From other apps", "AdobeARMservice", "SomeVendorSvc"],
            Build(ServiceFilter.Default, ticked, [fax]));
        Assert.Contains("Fax", Build(new ServiceFilter(ShowNotRunning: true), ticked, [fax]));
    }

    [Fact]
    public void TheSearchFindsANameItsCatalogueNameOrWhatItLosesInEitherCase()
    {
        Assert.Equal(["# Windows services", "edgeupdatem"], Build(new ServiceFilter("UpdateM", ShowNotRunning: true)));
        Assert.Equal(["# Windows services", "SysMain"], Build(new ServiceFilter("superfetch", ShowNotRunning: true))); // its catalogue name
        Assert.Equal(["# Windows services", "WSearch"], Build(new ServiceFilter("START-MENU", ShowNotRunning: true)));  // what it loses
        Assert.Empty(Build(new ServiceFilter("nothing like it", ShowNotRunning: true, ShowProtected: true)));
        Assert.Equal(["# Windows services", "DiagTrack", "WSearch"], Build(new ServiceFilter("  ")).Take(3)); // blank: everything shown
    }

    [Fact]
    public void AWindowsServiceIsOneTheCatalogueKnowsWindowsOwnStartTypeFor()
    {
        var o = SomeServices.Options().ToDictionary(x => x.Service.Name);
        Assert.Equal(ServiceRows.WindowsId, ServiceRows.GroupOf(o["edgeupdate"]));     // Microsoft's, with a Windows default
        Assert.Equal(ServiceRows.AppsId, ServiceRows.GroupOf(o["AdobeARMservice"]));    // in the catalogue, no Windows default
        Assert.Equal(ServiceRows.AppsId, ServiceRows.GroupOf(o["SomeVendorSvc"]));      // not in the catalogue
        Assert.Equal(ServiceRows.ProtectedId, ServiceRows.GroupOf(o["WinDefend"]));
    }

    [Fact]
    public void EachGroupsHeaderSaysWhereItsAdviceComesFrom()
    {
        var headers = ServiceRows.Build(SomeServices.Rows(), new ServiceFilter(ShowProtected: true), None, 0, true, None).OfType<ServiceGroupRow>().ToList();
        Assert.Equal(("Windows services", "These sources were written for virtual desktops and company PCs. Check what you lose first."), (headers[0].Name, headers[0].Note));
        Assert.Equal(("From other apps", "No official source: decide with your AI agent"), (headers[1].Name, headers[1].Note));
        Assert.Equal("Windows services: These sources were written for virtual desktops and company PCs. Check what you lose first. 0 of 2 ticked, expanded",
            headers[0].AccessibleText);
        foreach (var h in headers) Assert.DoesNotContain("safe", h.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachWindowsRowSaysWhatItsSourceIsForAndOtherAppsOnlyWhenTheyHaveOne()
    {
        static CatalogueEntry E(string name, ServiceSourceKind kind, bool windows) => new()
        {
            Services = [name], Name = name, Category = "Test", Lose = "something",
            WindowsDefaults = windows ? new() { [name] = "manual" } : null, Source = new ServiceSource(kind),
        };
        var cat = new ServiceCatalogue([E("WinA", ServiceSourceKind.Wdot, true), E("WinB", ServiceSourceKind.Cis, true), E("WinC", ServiceSourceKind.Both, true),
            E("WinD", ServiceSourceKind.None, true), E("AppA", ServiceSourceKind.Cis, false), E("AppB", ServiceSourceKind.None, false)]);
        var options = ServicePlan.Options(new[] { "WinA", "WinB", "WinC", "WinD", "AppA", "AppB", "Other" }
            .Select(n => new ServiceView { Name = n, StartType = ServiceStartType.Manual, Running = true, MicrosoftSigned = n.StartsWith("Win") }), cat, false, new HashSet<string>());
        var source = options.ToDictionary(o => o.Service.Name, ServiceRows.SourceText);
        Assert.Equal(ServiceSourceText.WdotLabel, source["WinA"]);
        Assert.Equal(ServiceSourceText.CisLabel, source["WinB"]);
        Assert.Equal(ServiceSourceText.BothLabel, source["WinC"]);
        Assert.Equal(ServiceSourceText.NoneLabel, source["WinD"]);
        Assert.Equal(ServiceSourceText.CisLabel, source["AppA"]); // another app's entry that has a source says it
        Assert.Equal("", source["AppB"]);                          // its group already says it has none
        Assert.Equal("", source["Other"]);                         // not in the catalogue
        foreach (var s in source.Values)
        {
            Assert.DoesNotContain("safe", s, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("Microsoft's guidance", s);
        }
    }

    [Fact]
    public void TheCellsSayTheServiceWhatItLosesItsCostItsStateAndItsSource()
    {
        Assert.Equal(["Service", "You lose", "Cost", "State", "Source"], ServiceRows.Columns);
        var o = SomeServices.Options().ToDictionary(x => x.Service.Name);
        var row = ServiceRows.RowOf(o["WSearch"], ServiceRows.CostOf(o["WSearch"].Service with { Cores = 1.2, ProcessId = 7 }));
        Assert.Equal(["WSearch", "fast Start-menu / Explorer / Outlook search", "Medium: 1.2 cores", "running", ServiceSourceText.NoneLabel], row.Cells);
        Assert.Equal(ImpactLevel.Medium, row.Level);
        Assert.Equal(Color.DarkGoldenrod, row.Fore); // check first
        Assert.Equal("never changed (Windows security)", ServiceRows.LoseText(o["WinDefend"]));
        Assert.Equal("not known", ServiceRows.LoseText(o["SomeVendorSvc"]));
        Assert.Equal("not known (signed by Some Vendor)", ServiceRows.LoseText(o["SomeVendorSvc"] with { Service = o["SomeVendorSvc"].Service with { Publisher = "Some Vendor" } }));
        var xbl = ServicePlan.Options([new ServiceView { Name = "XblAuthManager", DisplayName = "Xbox Live Auth Manager", MicrosoftSigned = true }],
            SomeServices.Cat, insiderEnrolled: false, new HashSet<string>()).Single();
        Assert.Equal("Xbox Live", xbl.Catalogue?.Name); // its own row
        Assert.Equal("Xbox Live Auth Manager (XblAuthManager)", ServiceRows.ServiceText(xbl));
        Assert.Equal(("not running", (ImpactLevel?)null), ServiceRows.CostOf(o["SysMain"].Service));
        Assert.Equal(("None: idle", ImpactLevel.None), ServiceRows.CostOf(o["DiagTrack"].Service with { ProcessId = 5 }));
        // Before PoolWatch's first sampling window nothing is measured: "measuring", no meter, never "idle".
        Assert.Equal(("measuring…", (ImpactLevel?)null), ServiceRows.CostOf(o["DiagTrack"].Service with { ProcessId = 5 }, measured: false));
        Assert.Equal(("not running", (ImpactLevel?)null), ServiceRows.CostOf(o["SysMain"].Service, measured: false));
    }

    [Fact]
    public void ARowsTipIsItsNoteThenItsWholeSourceLine()
    {
        // The Source column is cut short in a narrow window: the tip has the whole line, after what the service is.
        var o = SomeServices.Options().ToDictionary(x => x.Service.Name);
        var diag = o["DiagTrack"];
        Assert.Equal($"{diag.Catalogue!.Note}\n{ServiceRows.SourceText(diag)}", ServiceRows.TipOf(diag));
        Assert.Equal(ServiceRows.TipOf(diag), ServiceRows.RowOf(diag, ("", null)).Tip);
        var described = o["SomeVendorSvc"] with { Service = o["SomeVendorSvc"].Service with { Description = "Keeps the vendor's tools up to date." } };
        Assert.Equal("Keeps the vendor's tools up to date.", ServiceRows.TipOf(described)); // another app's: no source line
        Assert.Null(ServiceRows.TipOf(o["SomeVendorSvc"]));
        Assert.EndsWith(ServiceSourceText.NoneLabel, ServiceRows.TipOf(o["WSearch"])); // a Windows service with no source says so too
    }

    [Fact]
    public void AGroupsTickIsAllSomeOrNoneOfItsShownServices()
    {
        CheckState Tick(params string[] ticked) => ServiceRows.Build(SomeServices.Rows(), ServiceFilter.Default, None, 0, true,
            new HashSet<string>(ticked, StringComparer.OrdinalIgnoreCase)).OfType<ServiceGroupRow>().First().Tick;
        Assert.Equal(CheckState.Unchecked, Tick());
        Assert.Equal(CheckState.Indeterminate, Tick("WSearch"));
        Assert.Equal(CheckState.Checked, Tick("DiagTrack", "WSearch"));
        Assert.Equal("1 service, never changed", new ServiceGroupRow("protected", "P", "n", true, 1, 0, 0, false).CountText);
        Assert.Equal("2 of 3 ticked", new ServiceGroupRow("windows", "W", "n", false, 3, 3, 2, false).CountText);
    }

    [Fact]
    public void AFoldedGroupKeepsItsHeaderAndItsCount()
    {
        var rows = ServiceRows.Build(SomeServices.Rows(), ServiceFilter.Default, new HashSet<string> { ServiceRows.WindowsId }, 0, true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DiagTrack" });
        var shown = Shown(rows);
        Assert.DoesNotContain("DiagTrack", shown);
        var windows = rows.OfType<ServiceGroupRow>().First();
        Assert.Equal(("1 of 2 ticked", CheckState.Indeterminate, true), (windows.CountText, windows.Tick, windows.Collapsed));
    }

    [Fact]
    public void ASortByCostOrdersByWhatItCostsAndASecondClickReverses()
    {
        var filter = new ServiceFilter(ShowNotRunning: true);
        var up = Build(filter, column: ServiceRows.CostColumn);
        Assert.Equal(["# Windows services", "edgeupdate", "edgeupdatem", "Fax", "InventorySvc", "PcaSvc", "SysMain", "DiagTrack", "WSearch",
            "# From other apps", "AdobeARMservice", "SomeVendorSvc"], up);
        var down = Build(filter, column: ServiceRows.CostColumn, ascending: false);
        Assert.Equal(["# Windows services", "WSearch", "DiagTrack", "SysMain", "PcaSvc", "InventorySvc", "Fax", "edgeupdatem", "edgeupdate",
            "# From other apps", "SomeVendorSvc", "AdobeARMservice"], down);
    }

    [Fact]
    public void ASortByNameIgnoresCaseAndWithinAGroupTheRowsFollowTheSort()
    {
        Assert.Equal(["# Windows services", "DiagTrack", "edgeupdate", "edgeupdatem", "Fax"], Build(new ServiceFilter(ShowNotRunning: true)).Take(5));
        Assert.Equal(["# Windows services", "WSearch", "SysMain", "PcaSvc"], Build(new ServiceFilter(ShowNotRunning: true), ascending: false).Take(4));
    }

    [Fact]
    public void TheShownLineCountsTheServicesTheFilterShows() =>
        Assert.Equal(("Showing 4 of 12 services", "Showing 1 of 1 service"), (ServiceRows.ShownText(4, 12), ServiceRows.ShownText(1, 1)));
}

/// <summary>The list control itself, in a form that is never shown.</summary>
public sealed class ServiceListViewTests : IDisposable
{
    private readonly Form _host = new() { Size = new Size(1000, 700) };
    private readonly ServiceListView _list = new() { Dock = DockStyle.Fill };
    private int _changes;

    public ServiceListViewTests()
    {
        foreach (var c in ServiceRows.Columns) _list.Columns.Add(c, 120);
        _host.Controls.Add(_list);
        _host.CreateControl();
        _ = _list.Handle;
        _list.SetRows(SomeServices.Rows());
        _list.TicksChanged += (_, _) => _changes++;
    }

    public void Dispose() => _host.Dispose();

    private ListViewItem ItemOf(string service) => _list.Items.Cast<ListViewItem>().Single(i => i.Tag is ServiceOption o && o.Service.Name == service);
    private ListViewItem HeaderOf(string id) => _list.Items.Cast<ListViewItem>().Single(i => i.Tag is ServiceGroupRow g && g.Id == id);
    private List<string> Names() => _list.Items.Cast<ListViewItem>().Select(i => i.Tag is ServiceOption o ? o.Service.Name : "# " + ((ServiceGroupRow)i.Tag!).Id).ToList();

    private void Select(params string[] keys)
    {
        _list.SelectedItems.Clear();
        foreach (ListViewItem it in _list.Items) if (keys.Contains(ServiceListView.KeyOf(it))) it.Selected = true;
    }

    [Fact]
    public void AtFirstItHidesWhatIsNotRunningAndWhatIsProtected()
    {
        Assert.Equal(ServiceFilter.Default, _list.Filter);
        Assert.Equal(["# windows", "DiagTrack", "WSearch", "# apps", "AdobeARMservice", "SomeVendorSvc"], Names());
        Assert.Equal(4, _list.ShownCount);
        Assert.Equal(12, _list.Rows.Count);
        Assert.Equal("Services", _list.AccessibleName);
    }

    [Fact]
    public void ANewFilterShowsTheRowsAgainAndKeepsTheTicks()
    {
        _list.SetTicks(["DiagTrack"]);
        _list.Filter = new ServiceFilter(ShowNotRunning: true, ShowProtected: true);
        Assert.Equal(15, _list.Items.Count); // three headers and the twelve services
        Assert.Contains("RpcSs", Names());
        _list.Filter = new ServiceFilter("wsearch");
        Assert.Equal(["# windows", "WSearch"], Names());
        Assert.Equal(["DiagTrack"], _list.Ticked);                              // a hidden row keeps its tick
        _list.Filter = ServiceFilter.Default;
        Assert.True(ItemOf("DiagTrack").Checked);
    }

    [Fact]
    public void AGroupsTickTicksOnlyTheServicesItShows()
    {
        // SysMain, PcaSvc, Fax, ... are Windows services too, but not running and so not shown: the header's tick never
        // turns off what the owner cannot see.
        _list.ToggleGroup(ServiceRows.WindowsId);
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
        Assert.Equal(1, _changes);
        _list.ToggleGroup(ServiceRows.WindowsId);
        Assert.Empty(_list.Ticked);
        _list.Filter = new ServiceFilter(ShowNotRunning: true);
        _list.ToggleGroup(ServiceRows.WindowsId);
        Assert.Equal(["DiagTrack", "InventorySvc", "PcaSvc", "SysMain", "WSearch", "Fax", "edgeupdatem", "edgeupdate"], _list.Ticked);
    }

    [Fact]
    public void ATickedServiceThatIsNotRunningShowsOnceTicked()
    {
        _list.SetTicks(["SysMain"]);          // a profile picked it
        Assert.Contains("SysMain", Names());
        Assert.True(ItemOf("SysMain").Checked);
    }

    [Fact]
    public void AClickedTickIsKeptAndTheHeaderCountsIt()
    {
        ItemOf("WSearch").Checked = true; // as a click on its box does
        Assert.Equal(["WSearch"], _list.Ticked);
        Assert.Equal(1, _changes);
        Assert.StartsWith("Windows services: ", HeaderOf(ServiceRows.WindowsId).Text);
        Assert.EndsWith(" 1 of 2 ticked, expanded", HeaderOf(ServiceRows.WindowsId).Text);
        Assert.Equal(CheckState.Indeterminate, ((ServiceGroupRow)HeaderOf(ServiceRows.WindowsId).Tag!).Tick);
    }

    [Fact]
    public void TicksSurviveAFoldASortAndARefresh()
    {
        _list.SetTicks(["DiagTrack", "SomeVendorSvc"]);
        _list.SetCollapsed(ServiceRows.WindowsId, true);
        Assert.DoesNotContain("DiagTrack", Names());
        Assert.Equal(["DiagTrack", "SomeVendorSvc"], _list.Ticked);           // a folded group's ticks count (Turn off turns them off)
        _list.SetCollapsed(ServiceRows.WindowsId, false);
        Assert.True(ItemOf("DiagTrack").Checked);
        _list.SortBy(ServiceRows.CostColumn);
        Assert.True(ItemOf("DiagTrack").Checked && ItemOf("SomeVendorSvc").Checked);
        _list.SetRows(SomeServices.Rows());                                     // Refresh, or after a change
        Assert.Equal(["DiagTrack", "SomeVendorSvc"], _list.Ticked);
        Assert.True(ItemOf("SomeVendorSvc").Checked);
    }

    [Fact]
    public void ARefreshDropsTheTickOfAServiceThatIsGone()
    {
        _list.SetTicks(["DiagTrack", "WSearch"]);
        int before = _changes;
        _list.SetRows(SomeServices.Rows().Where(r => r.Name != "WSearch").ToList());
        Assert.Equal(["DiagTrack"], _list.Ticked);
        Assert.Equal(before + 1, _changes);
        _list.SetRows(SomeServices.Rows());
        Assert.False(_list.IsTicked("WSearch"));
    }

    [Fact]
    public void AProtectedServiceNeverTicksAndAHeaderIsTickedOnlyThroughItsGroup()
    {
        _list.Filter = new ServiceFilter(ShowProtected: true);
        ItemOf("WinDefend").Checked = true;
        Assert.False(ItemOf("WinDefend").Checked);
        HeaderOf(ServiceRows.WindowsId).Checked = true; // Windows' own toggle of the header row: refused
        Assert.False(HeaderOf(ServiceRows.WindowsId).Checked);
        Assert.Empty(_list.Ticked);
        _list.SetTicks(["WinDefend", "WSearch"]);
        Assert.Equal(["WSearch"], _list.Ticked);
        _list.ToggleGroup(ServiceRows.ProtectedId);
        Assert.Equal(["WSearch"], _list.Ticked);
    }

    [Fact]
    public void AFullGroupStillReadsAsTickedAfterARebuildOrANewWindow()
    {
        _list.ToggleGroup(ServiceRows.WindowsId);
        _list.SortBy(ServiceRows.CostColumn);                                    // the rows again
        Assert.True(HeaderOf(ServiceRows.WindowsId).Checked);
        _list.SortBy(0);
        int changes = _changes;
        typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_list, null);
        Assert.Equal(changes, _changes);                                          // a new window is not the owner ticking anything
        Assert.True(HeaderOf(ServiceRows.WindowsId).Checked);                     // the items went back into the new window with their ticks
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
        Assert.Equal(1, ListSort.ArrowOf(_list, 0));                              // and the arrow with them
    }

    [Fact]
    public void ACostRefreshUpdatesTheCellsAndTheirMetersFoldedRowsToo()
    {
        _list.SetCollapsed(ServiceRows.WindowsId, true);
        _list.UpdateCosts(o => (o.Service.Name + " cost", o.Service.Name == "WSearch" ? ImpactLevel.High : ImpactLevel.Low));
        Assert.Equal("SomeVendorSvc cost", ItemOf("SomeVendorSvc").SubItems[ServiceRows.CostColumn].Text);
        Assert.Equal(ImpactLevel.Low, ItemOf("SomeVendorSvc").SubItems[ServiceRows.CostColumn].Tag);
        _list.SetCollapsed(ServiceRows.WindowsId, false);
        Assert.Equal(("WSearch cost", (object?)ImpactLevel.High), (ItemOf("WSearch").SubItems[ServiceRows.CostColumn].Text, ItemOf("WSearch").SubItems[ServiceRows.CostColumn].Tag));
    }

    [Fact]
    public void ASortByCostFollowsTheCostTheListShowsNow()
    {
        // A refresh while the tab shows: DiagTrack now takes 2 cores, more than WSearch's 1.2. Sorted by Cost, it comes first.
        _list.UpdateCosts(o => o.Service.Name switch { "DiagTrack" => ("High: 2.0 cores", ImpactLevel.High), "WSearch" => ("Medium: 1.2 cores", ImpactLevel.Medium), _ => ("None: idle", ImpactLevel.None) });
        _list.SortBy(ServiceRows.CostColumn);
        _list.SortBy(ServiceRows.CostColumn); // the second click: descending
        var first = _list.Items[1];           // under the Windows header
        Assert.Equal(("DiagTrack", "High: 2.0 cores"), (((ServiceOption)first.Tag!).Service.Name, first.SubItems[ServiceRows.CostColumn].Text));
    }

    [Fact]
    public void WindowsDrawsEveryCellButAHeaderRowAndTheCostCellsMeter()
    {
        using var bmp = new Bitmap(800, 40);
        using var g = Graphics.FromImage(bmp);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        bool DrawsItem(ListViewItem it)
        {
            var e = new DrawListViewItemEventArgs(g, it, new Rectangle(0, 0, 800, 20), it.Index, 0);
            typeof(ServiceListView).GetMethod("OnDrawItem", flags)!.Invoke(_list, [e]);
            return e.DrawDefault;
        }
        bool DrawsCell(ListViewItem it, int column)
        {
            var sub = new DrawListViewSubItemEventArgs(g, new Rectangle(0, 0, 100, 20), it, it.SubItems[column], it.Index, column, _list.Columns[column], 0);
            typeof(ServiceListView).GetMethod("OnDrawSubItem", flags)!.Invoke(_list, [sub]);
            return sub.DrawDefault;
        }
        Assert.True(DrawsItem(ItemOf("WSearch")));
        Assert.True(DrawsCell(ItemOf("WSearch"), ServiceRows.NameColumn));
        Assert.False(DrawsCell(ItemOf("WSearch"), ServiceRows.CostColumn)); // drawn here, with its meter
        Assert.False(DrawsItem(HeaderOf(ServiceRows.WindowsId)));
        Assert.False(DrawsCell(HeaderOf(ServiceRows.WindowsId), 1));
        var col = new DrawListViewColumnHeaderEventArgs(g, new Rectangle(0, 0, 100, 20), 0, _list.Columns[0], 0, SystemColors.WindowText, SystemColors.Window, _list.Font);
        typeof(ServiceListView).GetMethod("OnDrawColumnHeader", flags)!.Invoke(_list, [col]);
        Assert.True(col.DrawDefault);                                             // the headers and their sort arrow
    }

    [Fact]
    public void TheSourceCellIsGreyAndARowsTipIsItsNoteAndItsWholeSource()
    {
        var row = ItemOf("SomeVendorSvc");
        Assert.False(row.UseItemStyleForSubItems);
        Assert.Equal(ListTabStyle.FaintText, row.SubItems[ServiceRows.SourceColumn].ForeColor); // the system's grey with high contrast on
        Assert.Equal(Color.DarkGoldenrod, ItemOf("DiagTrack").SubItems[ServiceRows.SourceColumn].ForeColor); // a row's own colour wins
        var diag = SomeServices.Options().Single(o => o.Service.Name == "DiagTrack");
        Assert.Equal("On a Windows Insider build this may affect Insider flights.\n" + ServiceRows.SourceText(diag), ItemOf("DiagTrack").ToolTipText);
        Assert.True(_list.ShowItemToolTips);
    }

    [Fact]
    public void EveryRebuildSaysSoSoTheCountLineFollowsTheList()
    {
        int rebuilt = 0;
        _list.ListRebuilt += (_, _) => rebuilt++;
        _list.Filter = new ServiceFilter(ShowNotRunning: true);
        Assert.Equal((1, 10), (rebuilt, _list.ShownCount));
        _list.SetTicks(["DiagTrack"]);
        _list.SetCollapsed(ServiceRows.WindowsId, true);
        _list.SortBy(ServiceRows.CostColumn);
        _list.SetRows(SomeServices.Rows());
        Assert.Equal(5, rebuilt);
    }

    [Fact]
    public void SpaceTicksEverySelectedRowAndUnticksThemWhenAllAreTicked()
    {
        Select("DiagTrack", "WSearch");
        var key = new KeyEventArgs(Keys.Space);                                 // the key as the list gets it
        typeof(ServiceListView).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_list, [key]);
        Assert.True(key.Handled && key.SuppressKeyPress);                       // Windows' own Space (the focused row only) never runs
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
        Assert.True(ItemOf("DiagTrack").Checked && ItemOf("WSearch").Checked);
        Assert.True(_list.HandleKey(Keys.Space, Keys.None));
        Assert.Empty(_list.Ticked);
        ItemOf("WSearch").Checked = true;
        _list.HandleKey(Keys.Space, Keys.None);                                  // some ticked: all are ticked
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
    }

    [Fact]
    public void ASelectedHeaderStandsForItsGroupsShownServices()
    {
        Select(ServiceGroupRow.GroupKey(ServiceRows.AppsId));
        _list.HandleKey(Keys.Space, Keys.None);
        Assert.Equal(["SomeVendorSvc", "AdobeARMservice"], _list.Ticked);
        Assert.True(HeaderOf(ServiceRows.AppsId).Checked);                       // all of it: the header reads as ticked
        Assert.EndsWith(" 2 of 2 ticked, expanded", HeaderOf(ServiceRows.AppsId).Text);
    }

    [Fact]
    public void TheRightClickMenuTicksAndUnticksTheSelection()
    {
        var menu = _list.ContextMenuStrip!;
        var opening = menu.GetType().GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(["Tick selected", "Untick selected"], menu.Items.Cast<ToolStripItem>().Select(i => i.Text));
        _list.SelectedItems.Clear();
        opening.Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
        Assert.False(menu.Items[0].Enabled || menu.Items[1].Enabled);           // nothing selected: nothing to tick
        Select("DiagTrack", "WSearch");
        opening.Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
        Assert.True(menu.Items[0].Enabled && menu.Items[1].Enabled);
        menu.Items[0].PerformClick();
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
        menu.Items[1].PerformClick();
        Assert.Empty(_list.Ticked);
    }

    [Fact]
    public void AClickOnAHeadersArrowFoldsItAndOnItsTickTicksItsGroup()
    {
        var row = HeaderOf(ServiceRows.WindowsId).Bounds;
        var (chevron, tick) = _list.Parts(row);
        Assert.Null(_list.ClickAction(new Point(chevron.Right + 200, row.Top + row.Height / 2))); // its name: Windows selects the row
        _list.ClickAction(new Point(tick.Left + tick.Width / 2, tick.Top + tick.Height / 2))!();
        Assert.Equal(["DiagTrack", "WSearch"], _list.Ticked);
        _list.ClickAction(new Point(chevron.Left + chevron.Width / 2, row.Top + row.Height / 2))!();
        Assert.True(_list.IsCollapsed(ServiceRows.WindowsId));
        _list.DoubleClickAt(new Point(chevron.Right + 200, row.Top + row.Height / 2));            // a double-click on its name unfolds it
        Assert.False(_list.IsCollapsed(ServiceRows.WindowsId));
        Assert.Null(_list.ClickAction(ItemOf("WSearch").Bounds.Location + new Size(2, 2)));      // a service row: Windows' own click
    }

    [Fact]
    public void TheProtectedHeadersTickDoesNothing()
    {
        _list.Filter = new ServiceFilter(ShowProtected: true);
        var row = HeaderOf(ServiceRows.ProtectedId).Bounds;
        var (_, tick) = _list.Parts(row);
        Assert.Null(_list.ClickAction(new Point(tick.Left + tick.Width / 2, tick.Top + tick.Height / 2)));
    }

    [Fact]
    public void LeftRightAndEnterFoldAndUnfoldTheFocusedHeader()
    {
        HeaderOf(ServiceRows.WindowsId).Focused = true;
        Assert.True(_list.HandleKey(Keys.Left, Keys.None));
        Assert.True(_list.IsCollapsed(ServiceRows.WindowsId));
        Assert.True(HeaderOf(ServiceRows.WindowsId).Focused);                    // the header keeps the focus
        _list.HandleKey(Keys.Right, Keys.None);
        Assert.False(_list.IsCollapsed(ServiceRows.WindowsId));
        _list.HandleKey(Keys.Enter, Keys.None);
        Assert.True(_list.IsCollapsed(ServiceRows.WindowsId));
        ItemOf("SomeVendorSvc").Focused = true;
        Assert.False(_list.HandleKey(Keys.Left, Keys.None));                     // a service row: the list's own keys
        Assert.True(_list.HandleKey(Keys.A, Keys.Control));
        Assert.Equal(_list.Items.Count, _list.SelectedItems.Count);
    }

    [Fact]
    public void AHeaderClickSortsAndTheHeaderShowsTheDirection()
    {
        Assert.Equal((0, true), (_list.SortColumn, _list.SortAscending));
        Assert.Equal(1, ListSort.ArrowOf(_list, 0));
        _list.SortBy(0);
        Assert.Equal(-1, ListSort.ArrowOf(_list, 0));
        _list.SortBy(ServiceRows.CostColumn);
        Assert.Equal((ServiceRows.CostColumn, true), (_list.SortColumn, _list.SortAscending));
        _list.GetType().GetMethod("OnColumnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_list, [new ColumnClickEventArgs(ServiceRows.CostColumn)]);
        Assert.Equal((ServiceRows.CostColumn, false), (_list.SortColumn, _list.SortAscending));
    }

    [Fact]
    public void ARebuildKeepsTheSelection()
    {
        Select("WSearch");
        _list.SortBy(ServiceRows.CostColumn);
        Assert.Equal("WSearch", ((ServiceOption)Assert.Single(_list.SelectedItems.Cast<ListViewItem>()).Tag!).Service.Name);
    }

    [Fact]
    public void AHeaderRowIsDrawnWithItsArrowItsTickItsNameItsNoteAndItsCount()
    {
        List<int> Drawn(Func<Graphics, Rectangle, Rectangle> draw)
        {
            using var bmp = new Bitmap(900, 24, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            var area = draw(g, new Rectangle(0, 0, 900, 24));
            var px = new List<int>();
            for (int x = area.Left; x < area.Right; x++) for (int y = area.Top; y < area.Bottom; y++) px.Add(bmp.GetPixel(x, y).ToArgb());
            return px;
        }
        int Ink(Func<Graphics, Rectangle, Rectangle> draw) => Drawn(draw).Count(p => p != ListTabStyle.Panel.ToArgb());
        var open = new ServiceGroupRow(ServiceRows.WindowsId, ServiceRows.WindowsName, ServiceSourceText.GroupCaveat, false, 2, 2, 0, false);
        Rectangle Paint(Graphics g, Rectangle r, ServiceGroupRow row, Func<(Rectangle chevron, Rectangle tick), Rectangle> part)
        {
            _list.PaintGroup(g, r, row, selected: false, focusedItem: false);
            return part(_list.Parts(r));
        }
        Assert.True(Ink((g, r) => Paint(g, r, open, p => p.chevron)) > 0);                                     // the arrow
        Assert.True(Ink((g, r) => Paint(g, r, open, p => p.tick)) > 0);                                        // the box
        Assert.True(Ink((g, r) => Paint(g, r, open, p => new Rectangle(p.tick.Right + 4, 0, 100, 24))) > 0);   // the name
        Assert.True(Ink((g, r) => Paint(g, r, open, p => new Rectangle(300, 0, 200, 24))) > 0);                 // the note
        Assert.True(Ink((g, r) => Paint(g, r, open, p => new Rectangle(r.Right - 100, 0, 92, 24))) > 0);              // the count, at the right
        // Some ticked draws a mixed box, all ticked a ticked one, the protected group a greyed one: four different boxes.
        var none = Drawn((g, r) => Paint(g, r, open, p => p.tick));
        var some = Drawn((g, r) => Paint(g, r, open with { Ticked = 1 }, p => p.tick));
        var all = Drawn((g, r) => Paint(g, r, open with { Ticked = 2 }, p => p.tick));
        var greyed = Drawn((g, r) => Paint(g, r, open with { Protected = true }, p => p.tick));
        Assert.False(none.SequenceEqual(some));
        Assert.False(none.SequenceEqual(all));
        Assert.False(some.SequenceEqual(all));
        Assert.False(none.SequenceEqual(greyed));
        Assert.False(Drawn((g, r) => Paint(g, r, open, p => p.chevron)).SequenceEqual(Drawn((g, r) => Paint(g, r, open with { Collapsed = true }, p => p.chevron))));
        using var whole = new Bitmap(_list.Width, _list.Height);
        _list.DrawToBitmap(whole, new Rectangle(Point.Empty, _list.Size)); // the whole list draws, header rows, meters and all
    }
}

/// <summary>The sort every list shares (the Cost column by what it costs) and the header arrow it shows.</summary>
public class ListSortTests
{
    private static readonly string[][] Tasks =
    [
        ["task", "Updater", "enabled", "not running", "a.exe"],
        ["startup", "Sync", "enabled", "idle (1 process)", "b.exe"],
        ["app", "Build Tool", "running, no window in front", "0.7 core, 3k page faults/s (average over 30 min)", "c.exe"],
        ["startup", "Chat", "enabled", "0.1 core (2 processes)", "d.exe"],
        ["task", "Backup", "disabled", "not measurable (runs through cmd.exe)", "e.exe"],
        ["app", "Indexer", "running, no window in front", "1.2 cores (average over 30 min)", "f.exe"],
        ["task", "Telemetry", "enabled", "3k page faults/s (1 process)", "g.exe"],
    ];

    private static string[] Names(List<string[]> rows) => rows.Select(r => r[1]).ToArray();

    [Fact]
    public void ByTypeThenNameAndASecondClickReverses()
    {
        Assert.Equal(["Build Tool", "Indexer", "Chat", "Sync", "Backup", "Telemetry", "Updater"], Names(ListSort.Sorted(Tasks, r => r, 0, true, costColumn: 3, tieColumn: 1)));
        Assert.Equal(["Updater", "Telemetry", "Backup", "Sync", "Chat", "Indexer", "Build Tool"], Names(ListSort.Sorted(Tasks, r => r, 0, false, costColumn: 3, tieColumn: 1)));
    }

    [Fact]
    public void RowsTheSortCannotTellApartKeepTheirOrder()
    {
        string[][] same = [["task", "A", "x", "idle", "1"], ["task", "A", "x", "idle", "2"], ["task", "A", "x", "idle", "3"]];
        Assert.Equal(["1", "2", "3"], ListSort.Sorted(same, r => r, 1, true).Select(r => r[4]));
        Assert.Equal(["1", "2", "3"], ListSort.Sorted(same, r => r, 1, false).Select(r => r[4]));
    }

    [Fact]
    public void TheCostColumnSortsByWhatEachCosts() =>
        // Not running and not measurable both sort first, by name; then idle, page faults alone, and by cores.
        Assert.Equal(["Backup", "Updater", "Sync", "Telemetry", "Chat", "Build Tool", "Indexer"], Names(ListSort.Sorted(Tasks, r => r, 3, true, costColumn: 3, tieColumn: 1)));

    [Theory]
    [InlineData("1.7 cores, 887k page faults/s", 1.7)]
    [InlineData("0.1 core (host shared with 4 other services)", 0.1)]
    [InlineData("idle", 0)]
    [InlineData("idle (1 process)", 0)]
    [InlineData("12k page faults/s", 0.01)]
    [InlineData("not running", -1)]
    [InlineData("not measurable (runs through cmd.exe)", -1)]
    [InlineData("", -1)]
    [InlineData("High: 1.7 cores, 887k page faults/s", 1.7)] // the meter's word first
    [InlineData("Low: 0.2 core", 0.2)]
    [InlineData("None: idle", 0)]
    [InlineData("Low: 12k page faults/s", 0.01)]
    public void ACostCellSortsByItsCores(string text, double key) => Assert.Equal(key, ListSort.CostKey(text));

    [Fact]
    public void AHeaderClickSortsAndShowsTheArrow()
    {
        using var host = new Form();
        var list = new ListView { View = View.Details };
        foreach (var c in TaskRows.Columns) list.Columns.Add(c);
        host.Controls.Add(list);
        int sorted = 0;
        var sort = new ColumnSort(list, 0, true, () => sorted++);
        _ = list.Handle;
        Application.DoEvents();                                                   // the arrow goes on once the new window has its columns
        Assert.Equal(1, ListSort.ArrowOf(list, 0));
        sort.Click(0);
        Assert.Equal((0, false, 1, -1), (sort.Column, sort.Ascending, sorted, ListSort.ArrowOf(list, 0)));
        sort.Click(2);
        Assert.Equal((2, true, 2), (sort.Column, sort.Ascending, sorted));
        Assert.Equal((0, 1), (ListSort.ArrowOf(list, 0), ListSort.ArrowOf(list, 2)));
        list.GetType().GetMethod("OnColumnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(list, [new ColumnClickEventArgs(1)]);                         // Windows' own header click
        Assert.Equal((1, true, 3), (sort.Column, sort.Ascending, sorted));
    }
}

/// <summary>When the Services tab reads its list again, when its buttons wait and what its status line says: pure state, no window.</summary>
public class ServicesTabStateTests
{
    [Fact]
    public void TheServicesAreReadAtFirstAndWhenTheRecordMovesOn()
    {
        var s = new ServicesTabState();
        Assert.True(s.NeedsRead(3));
        s.ReadStarted(3);
        Assert.Equal((true, "Reading services…"), (s.Busy, s.Status("")));
        Assert.False(s.NeedsRead(4));                            // one runs
        s.ReadFinished();
        Assert.Equal((false, "idle"), (s.Busy, s.Status("idle")));
        Assert.False(s.NeedsRead(3));
        Assert.True(s.NeedsRead(4));                             // a profile at start, an Undo in Active changes
        s.Stale();
        Assert.True(s.NeedsRead(3));                             // Refresh
    }

    [Fact]
    public void ARefreshDuringAChangeNeverReadsAndTheButtonsWaitUntilItEnds()
    {
        var s = new ServicesTabState();
        s.ReadStarted(1);
        s.ReadFinished();
        s.ApplyStarted();
        Assert.Equal((true, "Applying…"), (s.Busy, s.Status("")));
        // The change saves each service as it goes, so the record's version moves on: no read starts while it runs.
        Assert.False(s.NeedsRead(2));
        Assert.False(s.NeedsRead(5));
        s.ApplyFinished();
        Assert.False(s.Busy);
        Assert.True(s.NeedsRead(5));                             // its end reads the list again
    }

    [Fact]
    public void AReadThatEndsDuringAChangeLeavesTheButtonsWaiting()
    {
        // A read already running when a change starts (a profile applied while the list was read): it ends first.
        var s = new ServicesTabState();
        s.ReadStarted(1);
        s.ApplyStarted();
        s.ReadFinished();
        Assert.Equal((true, "Applying…"), (s.Busy, s.Status("PoolWatch could not read the services")));
        s.ApplyFinished();
        Assert.Equal((false, "PoolWatch could not read the services"), (s.Busy, s.Status("PoolWatch could not read the services")));
        Assert.Equal(new ServiceButtons("Turn off 1 service", false, false, false, false, false, false), ServicesPageText.Buttons(true, 1, 0, null, null));
    }

    [Fact]
    public void ASecondChangeStartedDuringTheFirstKeepsTheButtonsWaitingUntilBothEnd()
    {
        // Save as profile's "turn them off now" during a Turn off: two starts, then one end, is still busy.
        var s = new ServicesTabState();
        s.ApplyStarted();
        s.ApplyStarted();
        s.ApplyFinished();
        Assert.Equal((true, "Applying…"), (s.Busy, s.Status("")));
        Assert.False(s.NeedsRead(3));
        s.ApplyFinished();
        Assert.False(s.Busy);
        Assert.True(s.NeedsRead(3));
        s.ApplyFinished();                                        // an end with nothing running never makes the count negative
        s.ApplyStarted();
        Assert.True(s.Busy);
    }
}
