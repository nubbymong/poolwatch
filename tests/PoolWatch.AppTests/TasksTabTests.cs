using System.Drawing.Imaging;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Tasks and startup apps tab without a window: its rows (made-up apps), the filters both off at first, the ranking
/// by what each costs now, the busy apps joined to their entries, the detail panel's words and buttons, and what the
/// switch refuses. The panel and the list are built but never shown.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public class TasksTabTests
{
    private const string DeckExe = @"C:\Program Files\Example Vendor\Deck\deck.exe";
    private static readonly ProcessKey DeckKey = new(4100, 133_000_000_000_000_000);

    private static TaskEntry Entry(string name, TaskKind kind = TaskKind.StartupApp, bool? enabled = true, bool microsoft = false, string? exe = DeckExe,
        string? publisher = "Example Vendor", string starts = "") =>
        new($"{kind}:{name}", kind, name, publisher, starts, exe is null ? "" : $"\"{exe}\" --background", enabled, microsoft,
            kind == TaskKind.ScheduledTask ? new ScheduledTaskView(@"\Vendor\" + name, name, publisher ?? "", enabled == true, exe ?? "", "")
            : kind == TaskKind.StartupApp ? new StartupView(name, exe ?? "", "HKCU Run", enabled == true, microsoft) : null, exe);

    private static TaskCost Running(double cores, long faults = 0, params ProcessKey[] ends) => new(true, true, cores, faults, Math.Max(1, ends.Length), ends);

    /// <summary>A startup app that runs now, matched by its full path: 0.2 of a core and 9,000 page faults a second.</summary>
    internal static TaskRow StreamDeck() => new(Entry("Example Deck"), Running(0.2, 9000, DeckKey));

    [Fact]
    public void WhatAnEntryCostsIsItsProgramsLatestSamplingWindow()
    {
        // "Cost now" is the latest window's rate (0.7 of a core here), never a longer average; before the first window
        // there is none, so the row says it is still measuring, never "idle".
        var live = new List<LiveImage> { new(DeckKey, "deck.exe", DeckExe) };
        var window = new ProcessLoad.Rates([new ProcessLoad.ProcessRate(DeckKey, "deck.exe", 0.7, 3000)], 0.1, 3000);
        var now = Assert.Single(TasksTab.EntryRows([Entry("Example Deck")], live, window)).Cost;
        Assert.Equal((true, 0.7, 3000L), (now.Measured, now.Cores, now.PageFaultsPerSec));
        Assert.False(Assert.Single(TasksTab.EntryRows([Entry("Example Deck")], live, null)).Cost.Measured);
        // And a service's Cost cell: "measuring…" until the first window, then its process's cost now.
        var svc = new ServiceOption
        {
            Service = new ServiceView { Name = "ExampleSvc", DisplayName = "Example", StartType = ServiceStartType.Automatic, Running = true, ProcessId = 4100 },
            Group = ServiceGroup.Unknown,
        };
        Assert.Equal((TaskRows.Measuring, (ImpactLevel?)null), ServicesTab.CostCellOf(svc, null, _ => (0.7, 3000), null));
        var (text, level) = ServicesTab.CostCellOf(svc, null, pid => pid == 4100 ? (0.7, 3000) : null, window);
        Assert.NotNull(level);
        Assert.StartsWith(ImpactScale.Word(level!.Value) + ":", text);
    }

    private static List<TaskRow> SomeRows() =>
    [
        StreamDeck(),
        new(Entry("Quiet Helper", exe: @"C:\Program Files\Quiet\helper.exe"), Running(0)),
        new(Entry("Updater", TaskKind.ScheduledTask, starts: "every day"), TaskCost.NotRunning),
        new(Entry("Script Task", TaskKind.ScheduledTask, exe: @"C:\Windows\System32\cmd.exe"), TaskCost.NotMeasurable),
        new(Entry("Cloud Sync", microsoft: true, publisher: null, exe: @"C:\Program Files\Microsoft Sync\sync.exe"), Running(0.1)),
        new(Entry("Old Tray", enabled: false), TaskCost.NotRunning),
        new(new TaskEntry("busy:7:1", TaskKind.Busy, "Build Server", null, "", @"C:\Tools\build.exe --watch", null, false,
            new BusyApp(new ProcessKey(7, 1), "build.exe", "Build Server", "--watch"), @"C:\Tools\build.exe"), new TaskCost(true, true, 0.9, 0, 1, [new ProcessKey(7, 1)], Average: true)),
    ];

    private static List<string> Names(IEnumerable<TaskRow> rows) => rows.Select(r => r.Entry.Name).ToList();

    [Fact]
    public void AtFirstItShowsWhatRunsRankedByCostAndHidesMicrosoftsOwn()
    {
        Assert.Equal(new TaskFilter(TaskSegment.All, "", false, false), TaskFilter.Default);
        // Most first; a row that cannot be measured shows (it may be running); not running and Microsoft's are hidden.
        Assert.Equal(["Build Server", "Example Deck", "Quiet Helper", "Script Task"], Names(TaskRows.Build(SomeRows(), TaskFilter.Default, TaskRows.CostColumn, false)));
    }

    [Fact]
    public void TheTwoShowTicksAddWhatIsNotRunningAndMicrosoftsOwn()
    {
        Assert.Equal(["Build Server", "Example Deck", "Quiet Helper", "Old Tray", "Script Task", "Updater"],
            Names(TaskRows.Build(SomeRows(), new TaskFilter(ShowNotRunning: true), TaskRows.CostColumn, false)));
        Assert.Equal(["Build Server", "Example Deck", "Cloud Sync", "Quiet Helper", "Script Task"],
            Names(TaskRows.Build(SomeRows(), new TaskFilter(ShowMicrosoft: true), TaskRows.CostColumn, false)));
    }

    [Fact]
    public void ASegmentShowsOneKindAndTheSearchItsWords()
    {
        var all = new TaskFilter(ShowNotRunning: true, ShowMicrosoft: true);
        Assert.Equal(["Example Deck", "Cloud Sync", "Quiet Helper", "Old Tray"], Names(TaskRows.Build(SomeRows(), all with { Segment = TaskSegment.StartupApps }, TaskRows.CostColumn, false)));
        Assert.Equal(["Script Task", "Updater"], Names(TaskRows.Build(SomeRows(), all with { Segment = TaskSegment.ScheduledTasks }, TaskRows.CostColumn, false)));
        Assert.Equal(["Build Server"], Names(TaskRows.Build(SomeRows(), all with { Segment = TaskSegment.Busy }, TaskRows.CostColumn, false)));
        Assert.Equal(["Example Deck", "Quiet Helper", "Old Tray", "Script Task", "Updater"], Names(TaskRows.Build(SomeRows(), all with { Search = "example vendor" }, TaskRows.CostColumn, false)));
        Assert.Equal(["Quiet Helper"], Names(TaskRows.Build(SomeRows(), all with { Search = @"QUIET\HELPER" }, TaskRows.CostColumn, false))); // its command
        Assert.Equal(["Script Task", "Updater"], Names(TaskRows.Build(SomeRows(), all with { Search = "scheduled task" }, TaskRows.CostColumn, false)));  // its kind
    }

    [Fact]
    public void AClickOnAHeaderSortsByItTiesByName()
    {
        var all = new TaskFilter(ShowNotRunning: true);
        Assert.Equal(["Build Server", "Example Deck", "Old Tray", "Quiet Helper", "Script Task", "Updater"], Names(TaskRows.Build(SomeRows(), all, TaskRows.NameColumn, true)));
        Assert.Equal(["Updater", "Script Task", "Quiet Helper", "Old Tray", "Example Deck", "Build Server"], Names(TaskRows.Build(SomeRows(), all, TaskRows.NameColumn, false)));
        // By kind: busy, scheduled tasks, startup apps; within a kind by name.
        Assert.Equal(["Build Server", "Script Task", "Updater", "Example Deck", "Old Tray", "Quiet Helper"], Names(TaskRows.Build(SomeRows(), all, TaskRows.KindColumn, true)));
        // Cheapest first: what cannot be measured or is not running, then idle, then by cores.
        Assert.Equal(["Old Tray", "Script Task", "Updater", "Quiet Helper", "Example Deck", "Build Server"], Names(TaskRows.Build(SomeRows(), all, TaskRows.CostColumn, true)));
    }

    [Fact]
    public void TheCellsSayTheNameTheKindTheCostWithItsLevelAndWhenItStarts()
    {
        var rows = SomeRows().ToDictionary(r => r.Entry.Name);
        Assert.Equal(["Example Deck", "Startup app", "Low: 0.2 core, 9k page faults/s", "at sign-in"], TaskRows.Cells(rows["Example Deck"]));
        Assert.Equal(["Quiet Helper", "Startup app", "None: idle", "at sign-in"], TaskRows.Cells(rows["Quiet Helper"]));
        Assert.Equal(["Updater", "Scheduled task", "not running", "every day"], TaskRows.Cells(rows["Updater"]));
        Assert.Equal(["Script Task", "Scheduled task", "not measurable", "on its schedule"], TaskRows.Cells(rows["Script Task"]));
        Assert.Equal(["Build Server", "Busy in the background", "Medium: 0.9 core", "running now"], TaskRows.Cells(rows["Build Server"]));
        Assert.Equal((ImpactLevel?)ImpactLevel.Low, rows["Example Deck"].Cost.Level);
        Assert.Null(rows["Updater"].Cost.Level);
    }

    [Fact]
    public void ABusyAppThatAStartupAppRunsIsThatRowNotOneOfItsOwn()
    {
        var busy = new TaskRow(new TaskEntry("busy:4100:1", TaskKind.Busy, "deck.exe", null, "", DeckExe, null, false, null, DeckExe),
            new TaskCost(true, true, 0.8, 0, 1, [DeckKey], Average: true));
        var other = SomeRows()[^1];
        var rows = TaskRows.Merge([StreamDeck(), SomeRows()[1]], [busy, other]);
        Assert.Equal(["Example Deck", "Quiet Helper", "Build Server"], Names(rows));
        Assert.True(rows[0].Heavy);
        Assert.False(rows[1].Heavy);
        Assert.Equal(["Build Server", "Example Deck"], Names(TaskRows.Build(rows, new TaskFilter(TaskSegment.Busy), TaskRows.CostColumn, false)));
    }

    [Fact]
    public void TheDetailPanelSaysTheSwitchIsReversibleAndEndingItIsNot()
    {
        var d = TaskRows.Detail(StreamDeck());
        Assert.Equal(("Example Deck", "by Example Vendor, starts at sign-in"), (d.Title, d.Sub));
        Assert.Equal("Using 0.2 of a core and 9,000 page faults a second right now (1 process).", d.CostLine);
        Assert.Equal("\"Don't start at sign-in\" is the same switch as Task Manager's Startup apps: nothing is deleted, and you can put it back. " +
                     "\"End it now\" closes it until it next starts, and can't be undone.", d.Explain);
        Assert.Equal($"\"{DeckExe}\" --background", d.Command);
        Assert.Equal(("Don't start at sign-in", true, true), (d.Toggle, d.CanEnd, d.CanOpen));
    }

    [Fact]
    public void ASwitchedOffStartupAppAndATaskSayTheirOwnSwitch()
    {
        var off = TaskRows.Detail(new TaskRow(Entry("Old Tray", enabled: false), TaskCost.NotRunning));
        Assert.Equal(("Start at sign-in again", false), (off.Toggle, off.CanEnd));
        Assert.Equal("It is switched off: it does not start at sign-in. \"Start at sign-in again\" switches it back on, the same switch as Task Manager's Startup apps.", off.Explain);
        Assert.Equal("Not running now.", off.CostLine);
        var task = TaskRows.Detail(new TaskRow(Entry("Updater", TaskKind.ScheduledTask, starts: "every day and at sign-in"), TaskCost.NotRunning));
        Assert.Equal(("Turn this task off", "by Example Vendor, runs every day and at sign-in"), (task.Toggle, task.Sub));
        Assert.Equal("\"Turn this task off\" is the same switch as Disable in Task Scheduler: nothing is deleted, and you can put it back.", task.Explain);
        var taskOff = TaskRows.Detail(new TaskRow(Entry("Updater", TaskKind.ScheduledTask, enabled: false, publisher: null), TaskCost.NotRunning));
        Assert.Equal(("Turn this task back on", "runs on its schedule"), (taskOff.Toggle, taskOff.Sub));
    }

    [Fact]
    public void MicrosoftsOwnAndABusyAppHaveNoSwitch()
    {
        var ms = TaskRows.Detail(new TaskRow(Entry("Cloud Sync", microsoft: true), Running(0.1, 0, DeckKey)));
        Assert.Equal((null, false), (ms.Toggle, ms.CanEnd));
        Assert.StartsWith("PoolWatch leaves Microsoft's own startup apps alone", ms.Explain);
        var busy = TaskRows.Detail(SomeRows()[^1]);
        Assert.Equal((null, true, "running in the background"), (busy.Toggle, busy.CanEnd, busy.Sub));
        Assert.Equal("Using 0.9 of a core on average over the last 30 minutes, with no window in front (1 process).", busy.CostLine);
        Assert.Equal("\"End it now\" closes it until it next starts, and can't be undone. Unsaved work in it is lost.", busy.Explain);
    }

    [Fact]
    public void EndItNowIsOfferedOnlyForProcessesMatchedByTheirFullPath()
    {
        // Running, but matched by file name only (no full path to compare): it could be another program of that name.
        var byName = TaskRows.Detail(new TaskRow(Entry("Example Deck"), Running(0.2)));
        Assert.False(byName.CanEnd);
        Assert.EndsWith("PoolWatch can't tell for sure which running program is this one, so it does not offer to end it.", byName.Explain);
        Assert.False(TaskRows.Detail(new TaskRow(Entry("Example Deck"), TaskCost.NotRunning)).CanEnd);
        Assert.False(TaskRows.Detail(new TaskRow(Entry("Bare", exe: "bare.exe"), Running(0.2))).CanOpen); // no full path to show
    }

    [Fact]
    public void TheCostLineSaysWhatItUsesInWords()
    {
        Assert.Equal("Idle right now (2 processes).", TaskRows.CostLine(new TaskRow(Entry("A"), new TaskCost(true, true, 0, 0, 2, []))));
        Assert.Equal("Using 1.7 cores right now (1 process).", TaskRows.CostLine(new TaskRow(Entry("A"), Running(1.7))));
        Assert.Equal("PoolWatch cannot tell what it uses: it runs through cmd.exe, and every other program that does would be counted too.",
            TaskRows.CostLine(new TaskRow(Entry("A", exe: @"C:\Windows\System32\cmd.exe"), TaskCost.NotMeasurable)));
    }

    [Fact]
    public void EntriesComeFromTheTasksAndStartupAppsAsRead()
    {
        var task = new ScheduledTaskView(@"\Example\Nightly", "Nightly", "Example Corp", true, @"%ProgramFiles%\Example\nightly.exe /run", "", "every day");
        var noAuthor = new ScheduledTaskView(@"\Example\Other", "Other", "", false, @"C:\Program Files\Some Maker\other.exe", "");
        var lnk = new StartupView("Example Tray.lnk", @"""C:\Users\someone\AppData\Local\Example Tray\tray.exe"" -min", "Startup folder (you)", true, false);
        var ms = new StartupView("Cloud Sync", @"C:\Program Files\Microsoft Sync\sync.exe", "HKLM Run", true, true);
        var entries = TasksTab.Entries([task, noAuthor], [lnk, ms], s => s.Replace("%ProgramFiles%", @"C:\Program Files"));
        Assert.Equal(["task:\\Example\\Nightly", "task:\\Example\\Other", "startup:Startup folder (you)\\Example Tray.lnk", "startup:HKLM Run\\Cloud Sync"], entries.Select(e => e.Key));
        Assert.Equal(("Nightly", "Example Corp", "every day", true, @"C:\Program Files\Example\nightly.exe"), (entries[0].Name, entries[0].Publisher, entries[0].Starts, entries[0].Enabled, entries[0].Exe));
        Assert.Equal("Some Maker", entries[1].Publisher);                       // no author: the folder it is installed in
        Assert.Equal(("Example Tray", "Example Tray", TaskKind.StartupApp), (entries[2].Name, entries[2].Publisher, entries[2].Kind)); // ".lnk" dropped
        Assert.True(entries[3].Microsoft);
        Assert.Same(lnk, entries[2].Source);                                     // what the switch acts on
    }

    [Fact]
    public void AnEntrysCostIsTheLiveProcessesRunningItsProgram()
    {
        var live = new List<LiveImage>
        {
            new(DeckKey, "deck.exe", DeckExe),
            new(new ProcessKey(4200, 9), "deck.exe", DeckExe),
            new(new ProcessKey(4300, 9), "helper.exe", null), // its path could not be read
        };
        (double, long)? Cost(ProcessKey k) => k.Pid == 4100 ? (0.3, 2000) : (0.1, 0);
        var deck = TasksTab.CostOf(Entry("Example Deck"), live, Cost);
        Assert.Equal((true, 2, 0.4, 2000L), (deck.Running, deck.Processes, Math.Round(deck.Cores, 6), deck.PageFaultsPerSec));
        Assert.Equal([DeckKey, new ProcessKey(4200, 9)], deck.EndKeys);          // both by their full path
        var helper = TasksTab.CostOf(Entry("Helper", exe: @"C:\Program Files\Quiet\helper.exe"), live, Cost);
        Assert.True(helper.Running);
        Assert.Empty(helper.EndKeys);                                            // matched by name only: never ended from here
        Assert.Equal(TaskCost.NotMeasurable, TasksTab.CostOf(Entry("Script", exe: @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"), live, Cost));
        Assert.Equal(TaskCost.NotRunning, TasksTab.CostOf(Entry("Gone", exe: @"C:\Program Files\Gone\gone.exe"), live, Cost));
        Assert.Equal(TaskCost.NotRunning, TasksTab.CostOf(Entry("Empty", exe: null), live, Cost));
    }

    [Fact]
    public void TheSwitchLeavesMicrosoftsOwnStartupAppsAlone()
    {
        bool called = false;
        string? SetStartup(string location, string name, bool on) { called = true; return null; }
        string? SetTask(string path, bool on) { called = true; return null; }
        var ms = new StartupView("Cloud Sync", "sync.exe", "HKCU Run", true, Microsoft: true);
        Assert.Equal(ServicesPageText.MicrosoftLeftAlone, ServicesPageText.Switch(ms, enable: false, records: null!, SetTask, SetStartup));
        Assert.False(called); // nothing recorded, nothing switched
        Assert.Equal("the Tasks and startup apps tab", ServicesPageText.Via);
    }

    [Fact]
    public void TheDetailPanelFitsItsTextAndHintsWhenNothingIsSelected()
    {
        using var host = new Form { Font = new Font("Segoe UI", 9f) };
        var panel = new TaskDetailPanel { Dock = DockStyle.Bottom };
        host.Controls.Add(panel);
        host.ClientSize = new Size(900, 500);
        host.CreateControl();
        panel.Display(null);
        Assert.Equal(("Select a row", false, false), (panel.Title.Text, panel.ToggleShown, panel.ButtonsShown));
        panel.Display(TaskRows.Detail(StreamDeck()));
        panel.PerformLayout();
        Assert.True(panel.ToggleShown && panel.ButtonsShown);
        Assert.Equal("Don't start at sign-in", panel.Toggle.Text);
        Assert.True(panel.End.Enabled && panel.Open.Enabled);
        Assert.Equal(panel.NeededHeight, panel.Height);
        Assert.True(panel.Explain.Bottom <= panel.Height && panel.Command.Bottom <= panel.Height);
        Assert.True(panel.Toggle.Left > panel.Explain.Right, "the buttons sit right of the text");
        Assert.True(panel.Toggle.Top < panel.End.Top && panel.End.Top < panel.Open.Top, "stacked: the switch, then End it now, then Open file location");
        var problems = new List<string>();
        MainForm.ClippedControls(panel, problems, WindowFitTests.OwnVisible);
        Assert.Empty(problems);
        // The same row again changes nothing; another row's words replace it.
        panel.Display(TaskRows.Detail(SomeRows()[^1]));
        Assert.False(panel.ToggleShown);
        Assert.Equal("Build Server", panel.Title.Text);
    }

    [Fact]
    public void WhileTheListIsReadAgainTheSwitchAndEndItNowWait()
    {
        using var host = new Form { Font = new Font("Segoe UI", 9f) };
        var panel = new TaskDetailPanel { Dock = DockStyle.Bottom };
        host.Controls.Add(panel);
        host.ClientSize = new Size(900, 500);
        host.CreateControl();
        var d = TaskRows.Detail(StreamDeck());
        // Until the read after a switch ends, the button still says what it did: a second click would repeat it.
        panel.Display(d, busy: true);
        Assert.Equal((false, false, true), (panel.Toggle.Enabled, panel.End.Enabled, panel.Open.Enabled));
        panel.Display(d);
        Assert.Equal((true, true), (panel.Toggle.Enabled, panel.End.Enabled));
        // A list with no row: the panel says why, with no buttons.
        panel.Display(null, empty: ("Nothing here is running now.", "Tick Show not running to see them all."));
        Assert.Equal(("Nothing here is running now.", "Tick Show not running to see them all.", false, false),
            (panel.Title.Text, panel.CostLine.Text, panel.ButtonsShown, panel.Command.Visible));
        panel.Display(null);
        Assert.Equal("Select a row", panel.Title.Text);
    }

    [Fact]
    public void TheEmptyListsLineIsKeptForItsPaint()
    {
        using var host = new Form();
        var list = new TaskListView { Dock = DockStyle.Fill };
        host.Controls.Add(list);
        _ = list.Handle;
        Assert.Equal("", list.EmptyText);
        list.EmptyText = "Nothing here is running now. Tick Show not running to see them all.";
        Assert.StartsWith("Nothing here is running now.", list.EmptyText);
        using var bmp = new Bitmap(300, 100);
        list.DrawToBitmap(bmp, new Rectangle(0, 0, 300, 100)); // drawing with no row throws nothing
    }

    [Fact]
    public void TheListDrawsTheNameWithItsPublisherAndTheCostWithItsMeter()
    {
        using var host = new Form();
        var list = new TaskListView { Dock = DockStyle.Fill };
        foreach (var c in TaskRows.Columns) list.Columns.Add(c, 150);
        host.Controls.Add(list);
        _ = list.Handle;
        var row = StreamDeck();
        var noPublisher = new TaskRow(Entry("Plain", publisher: null), Running(0.2));
        list.RowOf = k => k as string == row.Key ? row : k as string == noPublisher.Key ? noPublisher : null;
        list.Items.Add(new ListViewItem(TaskRows.Cells(row)) { Tag = row.Key });
        list.Items.Add(new ListViewItem(TaskRows.Cells(noPublisher)) { Tag = noPublisher.Key });
        using var bmp = new Bitmap(600, 40, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        bool Default(ListViewItem it, int column)
        {
            var e = new DrawListViewSubItemEventArgs(g, new Rectangle(0, 0, 150, 20), it, it.SubItems[column], it.Index, column, list.Columns[column], 0);
            typeof(TaskListView).GetMethod("OnDrawSubItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(list, [e]);
            return e.DrawDefault;
        }
        Assert.False(Default(list.Items[0], TaskRows.NameColumn));   // the name and, in grey, who it is from
        Assert.False(Default(list.Items[0], TaskRows.CostColumn));   // the meter before the cost
        Assert.True(Default(list.Items[0], TaskRows.KindColumn));
        Assert.True(Default(list.Items[1], TaskRows.NameColumn));    // no publisher: Windows draws the name
        using var whole = new Bitmap(list.Width, list.Height);
        list.DrawToBitmap(whole, new Rectangle(Point.Empty, list.Size));
    }
}
