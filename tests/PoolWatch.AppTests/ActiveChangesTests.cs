using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Active changes window's logic with no window shown (<see cref="ActiveChangesView"/>) and the window itself built
/// but never shown: its groups and buttons, what each row says (how it was, and what cannot be put back, by the window or
/// by uninstalling), Put back and Put all back over a ledger of fake sources (the order, a failure in the middle, a warning
/// you say No to), and its size on a small screen. No test changes a real task, service, setting or registry value.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public sealed class ActiveChangesTests : IDisposable
{
    private readonly ChangeFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>A source of fixed rows; each put-back is recorded and answers as told (or throws).</summary>
    private sealed class Source(List<string> calls, params ActiveChange[] rows) : IChangeSource
    {
        public Func<ActiveChange, UndoResult>? Answer;

        public IEnumerable<ActiveChange> List() => rows;

        public UndoResult Undo(ActiveChange change, bool confirmed)
        {
            calls.Add(change.Id + (confirmed ? " confirmed" : ""));
            return Answer?.Invoke(change) ?? new UndoResult(true, "back");
        }

        public ActiveChange Unread(string error) => new("unread", ChangeKind.Service, null, "not read", false, error);
    }

    private static ActiveChange Row(ChangeKind kind, string? id = null, bool canUndo = true) =>
        new(id ?? kind.ToString(), kind, null, "the " + kind, canUndo, canUndo ? null : "why not");

    private ChangeLedger Ledger(params IChangeSource[] sources) => new(sources, _f.Records, _f.Log);

    /// <summary>The window, built and laid out but never shown (no Load, so it reads nothing by itself).</summary>
    private ActiveChangesForm Window(ChangeLedger? ledger = null)
    {
        var form = new ActiveChangesForm(ledger ?? Ledger(), new UiActions(null!, null!, new SynchronizationContext()));
        _ = form.Handle;
        return form;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Where a control is in its form's client area.</summary>
    private static Rectangle InForm(Control c)
    {
        var at = c.Location;
        for (var p = c.Parent; p is not null and not Form; p = p.Parent) at.Offset(p.Location);
        return new Rectangle(at, c.Size);
    }

    // ---------------------------------------------------------------- the groups and the buttons

    [Fact]
    public void FeaturesComeFirstThenWhatPoolWatchTurnedOffThenTheRestEachWithItsOwnButton()
    {
        var logon = Row(ChangeKind.LogonTask);
        var clear = Row(ChangeKind.AutoClear);
        var guard = Row(ChangeKind.TokenGuard);
        var shortcut = Row(ChangeKind.Shortcut);
        var service = Row(ChangeKind.Service);
        var task = Row(ChangeKind.ScheduledTask);
        var toDefault = Row(ChangeKind.Service, "service:fax") with { Group = ChangeGroup.Other };

        var sections = ActiveChangesView.Sections([service, logon, toDefault, clear, task, shortcut, guard]);

        Assert.Equal([ChangeWords.FeaturesHeading, ChangeWords.TurnedOffHeading, ChangeWords.OtherHeading], sections.Select(s => s.Heading));
        Assert.Equal([logon, clear, shortcut, guard], sections[0].Rows); // each group in the list's own order
        Assert.Equal([service, task], sections[1].Rows);
        Assert.Equal([toDefault], sections[2].Rows);
        Assert.Equal(["Turn off", "Turn off", "Remove", "Turn off"], sections[0].Rows.Select(ActiveChangesView.ButtonText));
        Assert.All(sections[1].Rows.Concat(sections[2].Rows), c => Assert.Equal("Put back", ActiveChangesView.ButtonText(c)));
        // An empty group is not shown.
        Assert.Equal([ChangeWords.TurnedOffHeading], ActiveChangesView.Sections([service]).Select(s => s.Heading));
        Assert.Empty(ActiveChangesView.Sections([]));
    }

    [Fact]
    public void AnythingPoolWatchChangesCanBePutBackOneAtATimeOrAllAtOnce()
    {
        // Every kind of change has a row in one group, a button that says what it does, and Put all back takes it.
        var all = Enum.GetValues<ChangeKind>().Select(k => Row(k)).ToArray();
        foreach (var c in all)
        {
            Assert.Contains(ActiveChangesView.ButtonText(c), new[] { "Turn off", "Remove", "Put back" });
            Assert.False(string.IsNullOrWhiteSpace(ActiveChangesView.ButtonTip(c)));
            Assert.Single(ActiveChangesView.Sections([c]));
        }
        var calls = new List<string>();
        var ledger = Ledger(new Source(calls, all));
        foreach (var c in all) Assert.True(ActiveChangesView.PutBackOne(ledger, c).Done, c.Id);
        Assert.Equal(all.Select(c => c.Id).Order(), calls.Order()); // one at a time (with no warning, there is nothing to confirm)

        calls.Clear();
        var (text, done) = ActiveChangesView.PutAllBack(ledger, ActiveChangesView.PutAllBackPlan(all, _ => throw new InvalidOperationException("no warning to ask")));
        Assert.True(done, text);
        Assert.Equal(ChangeLedger.UndoOrder.Select(k => k + " confirmed"), calls); // all at once
    }

    [Fact]
    public void PutAllBackRunsInItsOrderAndOneThatFailsNeitherStopsTheRestNorGoesUnsaid()
    {
        var rows = Enum.GetValues<ChangeKind>().Reverse().Select(k => Row(k)).ToArray(); // listed in another order than it runs
        var calls = new List<string>();
        var source = new Source(calls, rows)
        {
            Answer = c => c.Kind == ChangeKind.Service ? throw new InvalidOperationException("the service control manager said no") : new UndoResult(true, "back"),
        };

        var (text, done) = ActiveChangesView.PutAllBack(Ledger(source), ActiveChangesView.PutAllBackPlan(rows, _ => true));

        Assert.False(done); // shown as a warning
        // Every change was tried, in Put all back's order (what depends on PoolWatch first, the logon task last), the one
        // that failed in its place and the ones after it all the same.
        Assert.Equal(ChangeLedger.UndoOrder.Select(k => k + " confirmed"), calls);
        Assert.StartsWith("Not done: the Service\nPoolWatch could not put this change back: the service control manager said no\n\n", text); // the failure first
        foreach (var k in Enum.GetValues<ChangeKind>().Where(k => k != ChangeKind.Service)) Assert.Contains($"Done: the {k}\nback", text);
    }

    [Fact]
    public void NothingIsPutBackUntilYouSayYesAndAWarningYouDeclineIsNeverRun()
    {
        var calls = new List<string>();
        var warned = Row(ChangeKind.RemovedClearTask) with { Warning = @"This task runs RAMMap from C:\Tools." };
        var plain = Row(ChangeKind.LogonTask);
        var ledger = Ledger(new Source(calls, warned, plain));

        using var form = Window(ledger);
        form.ShowList(ledger.List(), null);
        var asked = new List<string>();
        var plan = ActiveChangesView.PutAllBackPlan([warned, plain], w => { asked.Add(w); return false; });
        Assert.Empty(calls); // reading, showing and asking change nothing
        Assert.Equal([warned.Warning], asked);

        var (text, done) = ActiveChangesView.PutAllBack(ledger, plan);
        Assert.Equal(["LogonTask confirmed"], calls);
        Assert.False(done);
        Assert.StartsWith("Not done: the RemovedClearTask\nYou chose not to put it back.", text);
    }

    [Fact]
    public void OneChangeIsPutBackOnlyAsTheWindowShowedIt()
    {
        var shown = Row(ChangeKind.RemovedClearTask) with { Warning = @"This task runs RAMMap from C:\Tools." };
        var calls = new List<string>();
        var now = Ledger(new Source(calls, shown with { Warning = @"This task runs RAMMap from C:\Other." }));
        Assert.Equal((ActiveChangesView.ChangedSinceShown, false), ActiveChangesView.PutBackOne(now, shown));
        Assert.Empty(calls);
        Assert.Equal(("back", true), ActiveChangesView.PutBackOne(Ledger(new Source(calls, shown)), shown));
        Assert.Equal(["RemovedClearTask confirmed"], calls); // your yes to the warning you saw
    }

    // ---------------------------------------------------------------- what each row says

    [Fact]
    public void AChangePoolWatchCannotPutBackSaysSoAndWhyAndIsNeverTried()
    {
        var cannot = Row(ChangeKind.RemovedClearTask, canUndo: false) with
        {
            Detail = "scheduled task that ran RAMMap", Note = "It ran with your saved password, which PoolWatch never has.",
        };
        var lines = ActiveChangesView.Lines(cannot, CultureInfo.InvariantCulture);
        Assert.Equal([("scheduled task that ran RAMMap", LineKind.Detail), ("PoolWatch cannot put this back. It ran with your saved password, which PoolWatch never has.", LineKind.Caveat)], lines);
        Assert.Equal(ChangeWords.CannotPutBack, ActiveChangesView.ButtonTip(cannot));
        // Uninstalling cannot put it back either, and the footer no longer says it puts back all of them.
        Assert.Equal(cannot.Note, cannot.UninstallLeaves);
        Assert.Equal(ChangeWords.UninstallPutsBackTheOthers, ActiveChangesView.UninstallNote([cannot, Row(ChangeKind.LogonTask)]));
        Assert.Equal(ChangeWords.UninstallPutsAllBack, ActiveChangesView.UninstallNote([Row(ChangeKind.LogonTask)]));

        var calls = new List<string>();
        var (text, done) = ActiveChangesView.PutAllBack(Ledger(new Source(calls, cannot)), ActiveChangesView.PutAllBackPlan([cannot], _ => true));
        Assert.Empty(calls);
        Assert.False(done);
        Assert.Contains("Not done: the RemovedClearTask\nIt ran with your saved password", text);
        Assert.Contains("PoolWatch cannot put this back. It ran with your saved password", ActiveChangesView.PutAllBackQuestion([cannot]));
    }

    [Fact]
    public void AClearTaskPoolWatchCannotPutBackSaysSoOnce()
    {
        // Its own note already says PoolWatch cannot put it back (a task that ran with a saved password): the row, and
        // Put all back's question, say it once.
        _f.Records.Put(ChangeRecordFixtures.ClearTask(logonType: 1, user: "S-1-5-21-1-2-3-1001"));
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains("cannot put", row.Note, StringComparison.OrdinalIgnoreCase);
        var caveat = ActiveChangesView.Lines(row, CultureInfo.InvariantCulture)[^1];
        Assert.Equal((row.Note!, LineKind.Caveat), caveat);
        Assert.Single(Regex.Matches(caveat.Text, "cannot put", RegexOptions.IgnoreCase));
        Assert.Single(Regex.Matches(ActiveChangesView.PutAllBackQuestion([row]), "cannot put", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void AClearTaskThatNeedsYourYesSaysUninstallingDoesNotPutItBack()
    {
        // RAMMap in a folder a standard user can change: the window asks before putting it back, and the uninstaller,
        // which never says yes for you, leaves it (its definition is saved for import).
        _f.Records.Put(ChangeRecordFixtures.ClearTask(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")));
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.True(row.CanUndo);
        Assert.NotNull(row.Warning);
        Assert.Equal(ChangeWords.UninstallNeedsYourYes, row.UninstallLeaves);
        Assert.Equal((ChangeWords.UninstallLeaves + " " + ChangeWords.UninstallNeedsYourYes, LineKind.Caveat), ActiveChangesView.Lines(row, CultureInfo.InvariantCulture)[^1]);
        Assert.Equal(ChangeWords.UninstallPutsBackTheOthers, ActiveChangesView.UninstallNote([row]));
        Assert.Equal((ChangeGroup.TurnedOff, "Your clear task \"Clear standby\"", "Put back"), (row.Group, row.Title, ActiveChangesView.ButtonText(row)));
    }

    [Fact]
    public void AServiceTheProfileHoldsIsStillPutBackByUninstalling()
    {
        // Its warning is about the profile applied at start; the uninstaller puts the service back and leaves profiles alone.
        var held = Row(ChangeKind.Service) with { Warning = ChangeWords.KeptOffAtStart("Build box", "MapsBroker"), AtStart = new AtStartHold("Build box", "MapsBroker") };
        Assert.Null(held.UninstallLeaves);
        Assert.Null(Row(ChangeKind.LogonTask).UninstallLeaves);
        Assert.Equal(ChangeWords.UninstallPutsAllBack, ActiveChangesView.UninstallNote([held]));
    }

    [Fact]
    public void TurningTheTokenLeakGuardOffSaysTheWindowsSettingStaysUntilYouSignOut()
    {
        var live = _f.Live(new Settings { TokenGuardEnabled = true });
        var source = new SettingsSource(live, _f.Records, null);
        var row = Assert.Single(source.List());
        Assert.Equal((ChangeWords.TokenGuardTitle, ChangeGroup.Feature, "Turn off"), (row.Title, row.Group, ActiveChangesView.ButtonText(row)));
        Assert.Contains((ChangeWords.TokenGuardNote, LineKind.Detail), ActiveChangesView.Lines(row, CultureInfo.InvariantCulture)); // on its row
        Assert.Equal(ChangeWords.TokenGuardTurnOffTip, ActiveChangesView.ButtonTip(row));                                                // on its button, as what it does
        Assert.StartsWith("Turns the token-leak guard off.", ActiveChangesView.ButtonTip(row));
        Assert.Equal((ChangeWords.TokenGuardOff, true), ActiveChangesView.PutBackOne(Ledger(source), row));                          // and once it is off
        Assert.False(live.Running.TokenGuardEnabled);
    }

    [Fact]
    public void EachSourceGivesItsRowsATitleADetailAndAGroup()
    {
        var live = _f.Live(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1, AutoClearPoolGb = 8 });
        var clear = Assert.Single(new SettingsSource(live, _f.Records, null).List());
        Assert.Equal((ChangeWords.AutoClearTitle, "every hour, or at 8 GB", ChangeGroup.Feature), (clear.Title, clear.Detail, clear.Group));
        Assert.Equal("The cache is cleared every hour, or sooner when the kernel pool reaches 8 GB", clear.What); // the uninstaller's line is as before

        var logon = Assert.Single(new LogonTaskSource(new LogonTasks()).List());
        Assert.Equal((ChangeWords.LogonTaskTitle, ChangeWords.LogonTaskDetail, ChangeGroup.Feature), (logon.Title, logon.Detail, logon.Group));

        _f.Records.Put(ChangeRecordFixtures.Task());
        _f.Records.Put(ChangeRecordFixtures.Task(@"\Vendor\Helper") with { EnabledBefore = false }); // PoolWatch turned this one on
        _f.Records.Put(ChangeRecordFixtures.Startup());
        var recorded = _f.Recorded(live).List().ToDictionary(r => r.Id);
        var off = recorded[ChangeIds.ScheduledTask(@"\Vendor\Updater")];
        Assert.Equal((@"Vendor\Updater", "scheduled task · was on", ChangeGroup.TurnedOff), (off.Title, off.Detail, off.Group));
        var on = recorded[ChangeIds.ScheduledTask(@"\Vendor\Helper")];
        Assert.Equal(("scheduled task · was off", ChangeGroup.Other, "Put back"), (on.Detail, on.Group, ActiveChangesView.ButtonText(on)));
        var startup = recorded[ChangeIds.StartupEntry("HKCU Run", "Helper")];
        Assert.Equal(("Helper at sign-in", "startup app · was on", ChangeGroup.TurnedOff), (startup.Title, startup.Detail, startup.Group));
        // A row with a time says since when, after how it was.
        Assert.Equal("startup app · was on · " + ChangeWords.Since(startup.ChangedUtc!.Value.ToLocalTime(), CultureInfo.InvariantCulture),
            ActiveChangesView.Lines(startup, CultureInfo.InvariantCulture)[0].Text);

        var backend = new Services();
        var services = new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, backend);
        services.Apply(["MapsBroker"], "a suggestion");
        var service = Assert.Single(new ServicesSource(services).List());
        Assert.Equal(("Downloaded Maps Manager", "service · was Automatic and running", ChangeGroup.TurnedOff),
            (service.Title, service.Detail, service.Group));
        Assert.Equal("Turned off Downloaded Maps Manager (MapsBroker)", service.What); // the uninstaller's line still names the service

        var shortcutFolder = Path.Combine(_f.Root, "Desktop");
        _f.Records.Put(ChangeRecordFixtures.Shortcut(Path.Combine(shortcutFolder, "ChatGPT (no shader cache).lnk")));
        var shortcut = _f.Recorded(live, shortcutFolder).List().Single(r => r.Kind == ChangeKind.Shortcut);
        Assert.Equal(("Codex shortcut on the desktop", "starts Codex without its shader cache", ChangeGroup.Feature, "Remove"),
            (shortcut.Title, shortcut.Detail, shortcut.Group, ActiveChangesView.ButtonText(shortcut)));
    }

    [Fact]
    public void AServiceSetToItsWindowsDefaultIsAnotherChangeNotOneTurnedOff()
    {
        File.WriteAllText(Path.Combine(_f.Paths.DataRoot, "service-changes.json"), ServiceChangeLog.ToJson(
        [
            new ServiceChange
            {
                Service = "ExampleSvc", DisplayName = "Example Device Service", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false,
                NewStart = ServiceStartType.Manual, ChangedUtc = DateTime.UtcNow, Via = ServicePlan.ViaWindowsDefault,
            },
        ]));
        var services = new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, new Services());
        var row = Assert.Single(new ServicesSource(services).List());
        Assert.Equal(("Example Device Service", "service · set to its Windows default · was Disabled", ChangeGroup.Other, "Put back"),
            (row.Title, row.Detail, row.Group, ActiveChangesView.ButtonText(row)));
        Assert.Equal(ChangeWords.OtherHeading, Assert.Single(ActiveChangesView.Sections([row])).Heading);
    }

    private sealed class LogonTasks : ILogonTasks
    {
        public LogonTaskPolicy.ExistingTask? Read() => new(@"C:\Program Files\PoolWatch\PoolWatch.exe", "--tray", Highest: true);
        public string? RegisterCurrent() => throw new InvalidOperationException("never registered here");
        public string? Remove() => null;
    }

    private sealed class Services : IServiceBackend
    {
        private readonly Dictionary<string, ServiceView> _live = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MapsBroker"] = new ServiceView { Name = "MapsBroker", DisplayName = "Downloaded Maps Manager", StartType = ServiceStartType.Automatic, Running = true, MicrosoftSigned = true },
        };

        public IReadOnlyList<ServiceView> Enumerate() => _live.Values.ToList();
        public bool InsiderEnrolled() => false;
        public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            _live[op.Option.Service.Name] = _live[op.Option.Service.Name] with { StartType = op.NewStart, Running = false };
            return (null, null);
        }
        public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled) => (null, null);
        public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => null;
    }

    // ---------------------------------------------------------------- the window

    [Fact]
    public void TheWindowIsActiveChangesWithItsGroupsAndButtonsAndOffersNoRestorePoint()
    {
        var cannot = Row(ChangeKind.Shortcut, "shortcut:x", canUndo: false);
        var rows = new[] { Row(ChangeKind.LogonTask), Row(ChangeKind.Service), cannot };
        using var form = Window();
        form.ShowList(rows, null);

        Assert.Equal("Active changes", form.Text);
        var texts = Descendants(form).Select(c => c.Text).ToList();
        Assert.Contains("Active changes", texts);           // its heading
        Assert.Contains(ChangeWords.Lead, texts);
        Assert.Contains("FEATURES YOU TURNED ON", texts);
        Assert.Contains("TURNED OFF BY POOLWATCH", texts);
        Assert.Contains(ChangeWords.UninstallPutsBackTheOthers, texts); // the shortcut it cannot put back is not put back by uninstalling
        Assert.DoesNotContain(texts, t => t.Contains("restore point", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, t => t.Contains("Undo", StringComparison.Ordinal) || t.Contains("Applied changes", StringComparison.Ordinal));

        var buttons = Descendants(form).OfType<Button>().ToList();
        Assert.Equal(["Close", "Put all back", "Put back", "Remove", "Turn off"], buttons.Select(b => b.Text).Order(StringComparer.Ordinal));
        Assert.Equal("Remove", Assert.Single(buttons, b => !b.Enabled).Text); // the one it cannot put back is the only one disabled
        Assert.True(buttons.Single(b => b.Text == "Put all back").Enabled);
        Assert.Contains(texts, t => t.StartsWith(ChangeWords.CannotPutBack, StringComparison.Ordinal)); // and its row says so

        form.ShowList([], ChangeWords.NothingActive);
        Assert.False(Descendants(form).OfType<Button>().Single(b => b.Text == "Put all back").Enabled);
        Assert.Contains(ChangeWords.NothingActive, Descendants(form).Select(c => c.Text));
    }

    [Fact]
    public void TheFootersPromiseIsMadeOnlyOverRowsItCanPointAt()
    {
        Assert.Null(ActiveChangesView.UninstallNote([]));
        using var form = Window();
        string Promise() => string.Join(" | ", Descendants(form).Select(c => c.Text).Where(t => t.StartsWith("Uninstalling", StringComparison.Ordinal)));

        Assert.Equal("", Promise()); // before the list is read
        form.ShowList([Row(ChangeKind.LogonTask)], null);
        Assert.Equal(ChangeWords.UninstallPutsAllBack, Promise());
        form.ShowList([], ChangeWords.NothingActive); // nothing listed
        Assert.Equal("", Promise());
        form.ShowList([], "PoolWatch could not read the changes: access is denied"); // the list could not be read
        Assert.Equal("", Promise());
        var stats = form.SmokeStats();
        Assert.Equal("", stats.GetType().GetProperty("uninstallNote")!.GetValue(stats)); // what CI reads
    }

    [Fact]
    public void EachRowsButtonHasItsTipAndOldRowsLeaveNoTipBehind()
    {
        using var form = Window();
        var rows = new[] { Row(ChangeKind.TokenGuard), Row(ChangeKind.Service), Row(ChangeKind.Shortcut) };
        for (int i = 0; i < 3; i++) form.ShowList(rows, null); // the list is read again after each action

        var buttons = Descendants(form).OfType<Button>().ToList();
        Assert.Equal(ActiveChangesView.PutAllBackTip, form.Tips.GetToolTip(buttons.Single(b => b.Text == ChangeWords.PutAllBack)));
        foreach (var c in rows) // Turn off, Put back and Remove: one button each
            Assert.Equal(ActiveChangesView.ButtonTip(c), form.Tips.GetToolTip(buttons.Single(b => b.Text == ActiveChangesView.ButtonText(c))));
        // One tip per button now shown (the rows' and Put all back's), not one more for each row of each earlier list.
        Assert.Equal(rows.Length + 1, TipCount(form.Tips));
    }

    /// <summary>How many controls the tooltip holds a tip for (WinForms keeps them in a private dictionary keyed by control).</summary>
    private static int TipCount(ToolTip tips)
    {
        var table = typeof(ToolTip).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) && f.FieldType.GetGenericArguments()[0] == typeof(Control))
            .Select(f => f.GetValue(tips))
            .OfType<System.Collections.ICollection>()
            .ToList();
        Assert.True(table.Count == 1, "WinForms' ToolTip no longer keeps its tips in one dictionary keyed by control: update this test's TipCount");
        return table[0].Count;
    }

    [Fact]
    public void TheTimelineSaysWhatTheButtonDid()
    {
        var guard = Row(ChangeKind.TokenGuard) with { Title = ChangeWords.TokenGuardTitle };
        Assert.Equal("Turned off in Active changes: Token-leak guard. " + ChangeWords.TokenGuardOff,
            ChangeLedger.TimelineLine(guard, new UndoResult(true, ChangeWords.TokenGuardOff)));
        var shortcut = Row(ChangeKind.Shortcut) with { Title = ChangeWords.ShortcutTitle };
        Assert.Equal("Removed in Active changes: Codex shortcut on the desktop. Deleted it.", ChangeLedger.TimelineLine(shortcut, new UndoResult(true, "Deleted it.")));
        var task = Row(ChangeKind.ScheduledTask);
        Assert.Equal("Put back in Active changes: the ScheduledTask. back", ChangeLedger.TimelineLine(task, new UndoResult(true, "back")));
        Assert.Equal("Not put back in Active changes: the ScheduledTask. no", ChangeLedger.TimelineLine(task, new UndoResult(false, "no")));
        var toDefault = Row(ChangeKind.Service) with { Group = ChangeGroup.Other };
        Assert.StartsWith("Put back in Active changes: the Service.", ChangeLedger.TimelineLine(toDefault, new UndoResult(true, "back")));
    }

    [Fact]
    public void ThePrimaryButtonsPaddingIsGivenInTheWindowsOwnPixels()
    {
        using var b = new Button();
        ActiveChangesForm.Palette.Primary(b, 16); // 8 logical pixels at 200%
        if (!SystemInformation.HighContrast) Assert.Equal(new Padding(16, 0, 16, 0), b.Padding);
    }

    [Fact]
    public void TheWindowsSourceHasNoInvisibleCharacters()
    {
        // Its glyphs are written as escapes: a private-use character in the source cannot be told apart by a reader.
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && root is null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) root = dir.FullName;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root, "src", "PoolWatch", "Ui", "ActiveChangesForm.cs"));
        Assert.DoesNotContain(source, ch => ch >= (char)0xE000 && ch <= (char)0xF8FF);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void TheWindowFitsWholeOnA1024By600DesktopAtEveryScaling(int percent)
    {
        // The window and the desktop grow by the same factor with the scaling: a 1024 x 600 desktop (in logical pixels)
        // less a 48-pixel taskbar, and the window as it is built here at 100%, both scaled.
        using var form = Window();
        var window = new Size(form.Width * percent / 100, form.Height * percent / 100);
        var area = new Size(1024 * percent / 100, (600 - 48) * percent / 100);
        Assert.Equal(window, ActiveChangesView.Fit(window, area));
    }

    [Fact]
    public void OnAWorkingAreaShorterThanTheWindowTheListScrollsAndTheFooterStaysInView()
    {
        using var form = Window();
        var rows = Enumerable.Range(1, 30)
            .Select(i => Row(ChangeKind.Service, "service:s" + i) with { Title = $"Example Device Service {i}", Detail = "service · was Manual" }).ToList();
        form.ShowList(rows, null);
        var area = new Size(1024, 360); // a taller taskbar, or a smaller screen
        form.Size = ActiveChangesView.Fit(form.Size, area);
        form.PerformLayout();

        Assert.Equal(area.Height, form.Height);
        foreach (var name in new[] { "Put all back", "Close" })
        {
            var at = InForm(Descendants(form).OfType<Button>().Single(b => b.Text == name));
            Assert.True(at.Top >= 0 && at.Bottom <= form.ClientSize.Height && at.Left >= 0 && at.Right <= form.ClientSize.Width,
                $"{name} is at {at} in a client area of {form.ClientSize}");
        }
        var scroll = Descendants(form).OfType<UserControl>().Single();
        var list = scroll.Controls[0];
        Assert.True(scroll.AutoScroll && scroll.Height > 40, $"the list's area is {scroll.Size}");
        Assert.True(list.Height > scroll.ClientSize.Height, $"the list ({list.Size}) is taller than its area ({scroll.ClientSize}), so it scrolls");
        Assert.True(list.Width <= scroll.ClientSize.Width, $"the list ({list.Size}) fits the width of its area ({scroll.ClientSize})");
        // Never wider than the screen either.
        Assert.Equal(new Size(800, 300), ActiveChangesView.Fit(new Size(900, 300), new Size(800, 600)));
    }
}
