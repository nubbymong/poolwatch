using System.Security.Cryptography;
using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.AppTests;

/// <summary>
/// A data root in TEMP for the change tests (no ACL is touched), with its records, its settings and fakes for Task
/// Scheduler and the task / startup switches: no test changes a real task, service, registry value or setting.
/// </summary>
internal sealed class ChangeFixture : IDisposable
{
    public ChangeFixture()
    {
        Paths = new AppPaths(Path.Combine(Root, "data"), null, secure: false);
        Log = new Log(Path.Combine(Paths.DataRoot, "test.log"));
        Records = new ChangeRecords(Paths, Log);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pw-changes-" + Guid.NewGuid().ToString("N"));
    public AppPaths Paths { get; }
    public Log Log { get; }
    public ChangeRecords Records { get; }
    public FakeClearTasks ClearTasks { get; } = new();
    public FakeToggles Toggles { get; } = new();

    public LiveSettings Live(Settings? running = null, bool persist = true) => new(Paths, running ?? new Settings(), persist, Log);

    public Settings Saved() => Settings.Load(Paths.SettingsFile);

    public RecordedSource Recorded(LiveSettings live, string? shortcutFolder = null) =>
        new(Records, Toggles, ClearTasks, live, shortcutFolder is null ? null : [shortcutFolder]);

    public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
}

/// <summary>Task Scheduler's root folder, in memory.</summary>
internal sealed class FakeClearTasks : IClearTaskStore
{
    public readonly List<CacheHelper.LegacyTaskInfo> Tasks = new();
    public readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = new();
    /// <summary>Each definition as it was given to Register, exactly.</summary>
    public readonly List<string> RegisteredXml = new();
    public string? RegisterError;
    public Exception? FindError;
    public Action<CacheHelper.LegacyTaskInfo>? OnDelete;
    public Action? OnRegister;

    public IReadOnlyList<CacheHelper.LegacyTaskInfo> Find() => FindError is { } error ? throw error : Tasks.ToList();

    /// <summary>As Task Scheduler has it now, read again by name: removed only while it is still readable.</summary>
    public CacheHelper.LegacyTaskInfo? Delete(CacheHelper.LegacyTaskInfo task)
    {
        Calls.Add("delete " + task.Path);
        OnDelete?.Invoke(task);
        int i = Tasks.FindIndex(t => t.Path == task.Path && t.Xml is not null);
        if (i < 0) return null;
        var now = Tasks[i];
        Tasks.RemoveAt(i);
        return now;
    }

    /// <summary>Each name Undo asked about, in order.</summary>
    public readonly List<string> Looked = new();

    public bool Exists(string name)
    {
        Looked.Add(name);
        return Existing.Contains(name);
    }

    public string? Register(string name, string xml, int logonType, string userId)
    {
        Calls.Add($"register {name} as {logonType} {userId}");
        RegisteredXml.Add(xml);
        OnRegister?.Invoke();
        return RegisterError;
    }

    /// <summary>
    /// The access rules of the folders a task's RAMMap runs from, and of RAMMap itself, as SDDL made in memory (no folder
    /// or file on the PC is read or changed): a path not listed has those of Program Files, which only administrators may
    /// change.
    /// </summary>
    public readonly Dictionary<string, string> AccessRules = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each folder or file whose rules were read, in order.</summary>
    public readonly List<string> RulesRead = new();
    public UnelevatedAccount? Account = new(new(Sddl.SomeoneSid), []);

    public AdminPlaces Places => new([@"C:\Program Files", @"C:\Program Files (x86)"], Rules, Account);

    private System.Security.AccessControl.RawSecurityDescriptor? Rules(string path)
    {
        RulesRead.Add(path);
        return Sddl.Read(path, AccessRules.GetValueOrDefault(path, Sddl.ProgramFiles));
    }
}

/// <summary>Scheduled tasks and startup entries, in memory.</summary>
internal sealed class FakeToggles : ITaskToggles
{
    public readonly Dictionary<string, bool> Tasks = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> Startup = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = new();
    public string? Error;
    public bool Unreadable;
    /// <summary>Runs once, when a task is switched and before it is.</summary>
    public Action? OnSet;

    public string? SetTaskEnabled(string path, bool enabled)
    {
        Calls.Add($"task {path} {enabled}");
        if (OnSet is { } hook) { OnSet = null; hook(); }
        if (Error is null) Tasks[path] = enabled;
        return Error;
    }
    public string? SetStartupEnabled(string location, string name, bool enabled) { Calls.Add($"startup {location}\\{name} {enabled}"); if (Error is null) Startup[$"{location}\\{name}"] = enabled; return Error; }
    /// <summary>Runs once, when Undo asks whether a task is still there and before it is answered.</summary>
    public Action? OnExists;

    public bool TaskExists(string path)
    {
        if (OnExists is { } hook) { OnExists = null; hook(); }
        return Unreadable ? throw new InvalidOperationException("Task Scheduler is not available") : Tasks.ContainsKey(path);
    }

    public bool StartupExists(string location, string name) => Startup.ContainsKey($"{location}\\{name}");
}

/// <summary>
/// The ledger (one list over several sources, Undo, Undo all's fixed order) with fake sources, and the recorded changes:
/// applied-changes.json across a restart, task and startup switches, and the shortcut Undo on a file in TEMP.
/// </summary>
public sealed class ChangeLedgerTests : IDisposable
{
    private readonly ChangeFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>A source of fixed rows; Undo is recorded and answers as told (or throws).</summary>
    private sealed class FakeSource(List<string> calls, params ActiveChange[] rows) : IChangeSource
    {
        public Func<ActiveChange, UndoResult>? Answer;
        public bool ListThrows;

        public IEnumerable<ActiveChange> List() => ListThrows ? throw new InvalidOperationException("not readable") : rows;

        public UndoResult Undo(ActiveChange change, bool confirmed)
        {
            calls.Add(change.Id + (confirmed ? " confirmed" : ""));
            return Answer?.Invoke(change) ?? new UndoResult(true, "undone " + change.Id);
        }

        public ActiveChange Unread(string error) => new("unread", ChangeKind.Service, null, "not read", false, error);
    }

    private static ActiveChange Change(ChangeKind kind, int minutesAgo = -1, bool canUndo = true) =>
        new(kind.ToString(), kind, minutesAgo < 0 ? null : DateTime.UtcNow.AddMinutes(-minutesAgo), "the " + kind, canUndo, canUndo ? null : "why not");

    private ChangeLedger Ledger(params IChangeSource[] sources) => new(sources, _f.Records, _f.Log);

    [Fact]
    public void OneListNewestFirstWithStatesOnTop()
    {
        var calls = new List<string>();
        var ledger = Ledger(new FakeSource(calls, Change(ChangeKind.Service, 60), Change(ChangeKind.LogonTask)),
            new FakeSource(calls, Change(ChangeKind.Shortcut, 5), Change(ChangeKind.ScheduledTask, 300)));
        Assert.Equal(["LogonTask", "Shortcut", "Service", "ScheduledTask"], ledger.List().Select(c => c.Id));
    }

    [Fact]
    public void UndoAllRunsInTheFixedOrderAndNeverStops()
    {
        var calls = new List<string>();
        var kinds = Enum.GetValues<ChangeKind>().Reverse().ToArray(); // listed in another order than Undo all's
        var failing = new FakeSource(calls, kinds.Select(k => Change(k)).ToArray())
        {
            Answer = c => c.Kind switch
            {
                ChangeKind.Service => throw new InvalidOperationException("the service control manager said no"),
                ChangeKind.StartupEntry => new UndoResult(false, "it is still off"),
                _ => new UndoResult(true, "undone " + c.Id),
            },
        };
        var results = Ledger(failing).UndoAll();

        // What depends on PoolWatch first; the automatic clear off before your own clear task comes back; the logon task last.
        ChangeKind[] order =
        [
            ChangeKind.Shortcut, ChangeKind.StartupEntry, ChangeKind.ScheduledTask, ChangeKind.Service, ChangeKind.AutoClear,
            ChangeKind.RemovedClearTask, ChangeKind.TokenGuard, ChangeKind.LogonTask,
        ];
        Assert.Equal(order, ChangeLedger.UndoOrder);
        Assert.Equal(Enum.GetValues<ChangeKind>().Order(), order.Order()); // every kind has its one place
        Assert.Equal(order.Select(k => k.ToString()), calls);
        Assert.Equal(order, results.Select(r => r.Change.Kind));
        var service = results.Single(r => r.Change.Kind == ChangeKind.Service).Result;
        Assert.False(service.Done);
        Assert.Contains("the service control manager said no", service.Message);
        Assert.False(results.Single(r => r.Change.Kind == ChangeKind.StartupEntry).Result.Done);
        Assert.Equal(6, results.Count(r => r.Result.Done));
    }

    [Fact]
    public void UndoAllTakesTheFilterAndTheConfirmation()
    {
        var calls = new List<string>();
        var ledger = Ledger(new FakeSource(calls, Change(ChangeKind.Service), Change(ChangeKind.Shortcut), Change(ChangeKind.LogonTask)));
        var results = ledger.UndoAll(c => c.Kind is ChangeKind.LogonTask or ChangeKind.Shortcut, confirmed: true);
        Assert.Equal(["Shortcut confirmed", "LogonTask confirmed"], calls);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void AChangeThatCannotBeUndoneIsReportedNotTried()
    {
        var calls = new List<string>();
        var ledger = Ledger(new FakeSource(calls, Change(ChangeKind.RemovedClearTask, canUndo: false), Change(ChangeKind.LogonTask)));
        var results = ledger.UndoAll();
        Assert.Equal(["LogonTask"], calls);
        var refused = results.Single(r => r.Change.Kind == ChangeKind.RemovedClearTask).Result;
        Assert.Equal((false, "why not"), (refused.Done, refused.Message));
        var one = Assert.Single(ledger.UndoAll(c => c.Id == "RemovedClearTask")).Result; // as its own Undo button runs it
        Assert.Equal((false, "why not"), (one.Done, one.Message));
        Assert.Equal(["LogonTask"], calls);
    }

    [Fact]
    public void UndoOfOneChangeAndAnUnreadableSource()
    {
        var calls = new List<string>();
        var undone = new List<string>();
        var ledger = new ChangeLedger([new FakeSource(calls) { ListThrows = true }, new FakeSource(calls, Change(ChangeKind.AutoClear))],
            _f.Records, _f.Log, (c, r) => undone.Add($"{c.Id}: {r.Message}"));
        var unread = ledger.List().First();
        Assert.Equal(("unread", false, "not readable"), (unread.Id, unread.CanUndo, unread.Note)); // listed, never left out
        Assert.Equal(["unread", "AutoClear"], ledger.List().Select(c => c.Id)); // the other source still lists
        Assert.True(Assert.Single(ledger.UndoAll(c => c.Id == "AutoClear")).Result.Done);
        Assert.Equal(["AutoClear: undone AutoClear"], undone);
        Assert.Empty(ledger.UndoAll(c => c.Id == "gone")); // no longer listed: nothing runs, and nothing is reported done
        Assert.Equal(["AutoClear"], calls);

        var all = ledger.UndoAll();
        Assert.Equal((false, "not readable"), (all.Single(r => r.Change.Id == "unread").Result.Done, all.Single(r => r.Change.Id == "unread").Result.Message));
    }

    [Fact]
    public void EachSourceSaysWhatItCouldNotRead()
    {
        var live = _f.Live();
        IChangeSource[] sources =
        [
            new LogonTaskSource(new FakeLogonTasks()), new SettingsSource(live, _f.Records, null),
            new ServicesSource(new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, new FakeServices())), _f.Recorded(live),
        ];
        var rows = sources.Select(s => s.Unread("Access is denied.")).ToList();
        Assert.All(rows, r => Assert.False(r.CanUndo));
        Assert.All(rows, r => Assert.Equal(ChangeWords.UnreadNote("Access is denied."), r.Note));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal([ChangeKind.LogonTask, ChangeKind.AutoClear, ChangeKind.Service, ChangeKind.Shortcut], rows.Select(r => r.Kind));
        Assert.Equal("PoolWatch could not read the services it changed", rows[2].What);
    }

    [Fact]
    public void AListThatCannotBeReadIsAChangeUndoAllReports()
    {
        _f.Records.Put(ChangeRecordFixtures.ClearTask());
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"version\": 1", "\"version\": 2"));
        var ledger = new ChangeLedger([_f.Recorded(_f.Live())], _f.Records, _f.Log);

        var row = Assert.Single(ledger.List());
        Assert.Equal((ChangeKind.Shortcut, false), (row.Kind, row.CanUndo)); // an uninstall that keeps your other changes still reports it
        Assert.Contains(file, row.Note);
        var result = Assert.Single(ledger.UndoAll()).Result;
        Assert.False(result.Done);
    }

    [Fact]
    public void UndoAllAsksWithEveryChangeAndCaveatThenShowsEachResult()
    {
        var plain = Change(ChangeKind.LogonTask);
        var noted = Change(ChangeKind.AutoClear) with { Note = ChangeWords.AutoClearKeepsTaskNote };
        var warned = Change(ChangeKind.RemovedClearTask) with { Note = "a note", Warning = "This task runs RAMMap from C:\\Tools." };
        var refused = Change(ChangeKind.Shortcut, canUndo: false);
        string question = Ui.ActiveChangesView.PutAllBackQuestion([plain, noted, warned, refused]);
        foreach (var c in new[] { plain, noted, warned, refused }) Assert.Contains("- " + c.What, question);
        Assert.Contains(ChangeWords.AutoClearKeepsTaskNote, question);
        Assert.Contains("This task runs RAMMap from C:\\Tools.", question); // the warning, which a yes accepts
        Assert.DoesNotContain("a note", question);
        Assert.Contains("PoolWatch cannot put this back. why not", question);

        string results = Ui.ActiveChangesView.PutAllBackResults([(plain, new UndoResult(true, "gone")), (refused, new UndoResult(false, "why not"))]);
        Assert.Equal("Not done: the Shortcut\nwhy not\n\nDone: the LogonTask\ngone", results); // what was not done first
        Assert.Equal("There was nothing to put back.", Ui.ActiveChangesView.PutAllBackResults([]));
    }

    [Fact]
    public void UndoAllResultsListTheFirstTenAndCountTheRest()
    {
        static (ActiveChange, UndoResult) Result(int i, bool done) =>
            (Change(ChangeKind.Service) with { Id = "s" + i, What = $"change {i}." }, new UndoResult(done, done ? "back" : "still changed"));

        // 25 changes; the two that were not done ran near the end, where a long list would cut them off.
        string text = Ui.ActiveChangesView.PutAllBackResults(Enumerable.Range(1, 25).Select(i => Result(i, done: i is not (20 or 22))).ToList());
        Assert.StartsWith("Not done: change 20.\nstill changed\n\nNot done: change 22.\nstill changed\n\nDone: change 1.", text);
        Assert.Contains("Done: change 8.", text);
        Assert.DoesNotContain("change 9.", text);
        Assert.EndsWith("\n\nAnd 15 more, all done.", text);

        text = Ui.ActiveChangesView.PutAllBackResults(Enumerable.Range(1, 15).Select(i => Result(i, done: i > 12)).ToList());
        Assert.EndsWith("\n\nAnd 5 more: 2 not done, still listed in the window.", text);

        // One left over reads as plainly as many.
        text = Ui.ActiveChangesView.PutAllBackResults(Enumerable.Range(1, 11).Select(i => Result(i, done: true)).ToList());
        Assert.EndsWith("\n\nAnd 1 more, all done.", text);
        text = Ui.ActiveChangesView.PutAllBackResults(Enumerable.Range(1, 11).Select(i => Result(i, done: false)).ToList());
        Assert.EndsWith("\n\nAnd 1 more: 1 not done, still listed in the window.", text);
    }

    [Fact]
    public void UndoAllListsTheFirstTenChangesAndCountsTheRest()
    {
        var many = Enumerable.Range(1, 25).Select(i => Change(ChangeKind.Service) with { Id = "s" + i, What = $"change {i}." }).ToList();
        string question = Ui.ActiveChangesView.PutAllBackQuestion(many);
        Assert.Contains("- change 10.", question);
        Assert.DoesNotContain("- change 11.", question);
        Assert.EndsWith("And 15 more, listed in the window.", question);
        Assert.EndsWith("And 1 more, listed in the window.", Ui.ActiveChangesView.PutAllBackQuestion(many.Take(11).ToList()));
    }

    [Fact]
    public void UndoAllUndoesOnlyWhatTheWindowShowedAsItShowedIt()
    {
        var warned = Change(ChangeKind.RemovedClearTask) with { Warning = "This task runs RAMMap from C:\\Tools." };
        var plain = Change(ChangeKind.LogonTask);
        var shown = new[] { warned, plain };

        var calls = new List<string>();
        var now = new FakeSource(calls, warned with { Warning = "This task runs RAMMap from C:\\Other." }, plain, Change(ChangeKind.Shortcut)); // changed and added since
        var results = Ledger(now).UndoAll(Ui.ActiveChangesView.AsShown(shown), confirmed: true);
        Assert.Equal(["LogonTask confirmed"], calls);
        Assert.Equal([plain.Id], results.Select(r => r.Change.Id));

        calls.Clear();
        Ledger(new FakeSource(calls, warned, plain)).UndoAll(Ui.ActiveChangesView.AsShown(shown), confirmed: true);
        Assert.Equal(["RemovedClearTask confirmed", "LogonTask confirmed"], calls); // the warning you accepted, as you saw it

        // Undo all's question listed it as one that cannot be undone; it can be now, with no warning: it waits for the next look.
        var cannot = Change(ChangeKind.Shortcut, canUndo: false);
        calls.Clear();
        Ledger(new FakeSource(calls, cannot with { CanUndo = true }, plain)).UndoAll(Ui.ActiveChangesView.AsShown([cannot, plain]), confirmed: true);
        Assert.Equal(["LogonTask confirmed"], calls);
    }

    [Fact]
    public void UndoAllAsksEachWarningAndNeverRunsAChangeYouDeclined()
    {
        var declinedRow = Change(ChangeKind.RemovedClearTask) with { Id = @"clear-task:\A", Warning = @"This task runs RAMMap from C:\Tools." };
        var accepted = Change(ChangeKind.RemovedClearTask) with { Id = @"clear-task:\B", Warning = @"This task runs RAMMap from C:\Other." };
        var plain = Change(ChangeKind.LogonTask);
        var cannot = Change(ChangeKind.Shortcut, canUndo: false) with { Warning = "never asked" };
        var asked = new List<string>();
        var (which, declined) = Ui.ActiveChangesView.PutAllBackPlan([declinedRow, accepted, plain, cannot], w => { asked.Add(w); return w == accepted.Warning; });

        Assert.Equal([declinedRow.Warning, accepted.Warning], asked); // a row with no warning, or one that cannot be undone, is not asked about
        Assert.False(which(declinedRow));
        Assert.True(which(accepted));
        Assert.True(which(plain));
        var (change, result) = Assert.Single(declined);
        Assert.Equal((declinedRow, false, "You chose not to put it back."), (change, result.Done, result.Message));

        var calls = new List<string>();
        Ledger(new FakeSource(calls, declinedRow, accepted, plain)).UndoAll(which, confirmed: true);
        Assert.Equal([@"clear-task:\B confirmed", "LogonTask confirmed"], calls); // the one you declined is never run
    }

    [Fact]
    public void UndoAllSaysTheProfileAppliedAtStartOnceAndAsksNoMoreAboutIt()
    {
        ActiveChange Held(string service) => Change(ChangeKind.Service) with
        {
            Id = "service:" + service, What = $"Turned off {service} (profile Build box, at start)",
            Warning = ChangeWords.KeptOffAtStart("Build box", service), AtStart = new AtStartHold("Build box", service),
        };
        var maps = Held("MapsBroker");
        var sys = Held("SysMain");
        var clear = Change(ChangeKind.RemovedClearTask) with { Warning = @"This task runs RAMMap from C:\Tools." };
        var plain = Change(ChangeKind.LogonTask);

        string question = Ui.ActiveChangesView.PutAllBackQuestion([maps, sys, clear, plain]);
        Assert.DoesNotContain(maps.Warning!, question);             // not once a row
        Assert.Contains(clear.Warning!, question);
        Assert.EndsWith("\n\nBuild box, the profile PoolWatch applies when it starts, turns 2 of these services off again then: MapsBroker, SysMain. " +
                        "Put all back also takes them out of Build box, so they stay on.", question);

        // The yes accepts it: no box per service (each with No as its default), only the other warning is asked.
        var asked = new List<string>();
        var (which, declined) = Ui.ActiveChangesView.PutAllBackPlan([maps, sys, clear, plain], w => { asked.Add(w); return true; });
        Assert.Equal([clear.Warning], asked);
        Assert.True(which(maps) && which(sys) && which(clear) && which(plain));
        Assert.Empty(declined);
        var calls = new List<string>();
        Ledger(new FakeSource(calls, maps, sys, plain)).UndoAll(which, confirmed: true);
        Assert.Equal(["service:MapsBroker confirmed", "service:SysMain confirmed", "LogonTask confirmed"], calls);

        // Past the first ten rows too: the sentence counts and names every service the profile holds.
        var many = Enumerable.Range(1, 12).Select(i => Held($"S{i}")).ToList();
        string long12 = Ui.ActiveChangesView.PutAllBackQuestion(many);
        Assert.Contains("And 2 more, listed in the window.\n\nBuild box, the profile PoolWatch applies when it starts, turns 12 of these services off again then: S1, S2,", long12);
        Assert.EndsWith("S11, S12. Put all back also takes them out of Build box, so they stay on.", long12);
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns 1 of these services off again then: S1. Put all back also takes it out of " +
                     "Build box, so it stays on.", Ui.ActiveChangesView.PutAllBackQuestion([many[0]]).Split("\n\n")[^1]);
    }

    [Fact]
    public void TheDumpIsJsonWithKindsByName()
    {
        string json = ChangeLedger.ToJson([Change(ChangeKind.Service, 5), Change(ChangeKind.RemovedClearTask, canUndo: false)]);
        Assert.Contains("\"kind\": \"Service\"", json);
        Assert.Contains("\"canUndo\": false", json);
        Assert.Contains("\"note\": \"why not\"", json);
    }

    // ---------------------------------------------------------------- the records file

    [Fact]
    public void RecordsSurviveARestartAndOneInstancePerDataRoot()
    {
        var task = ChangeRecordFixtures.Task();
        _f.Records.Put(task);
        Assert.Equal([task], new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.Same(ChangeRecords.Open(_f.Paths, _f.Log), ChangeRecords.Open(new AppPaths(_f.Paths.DataRoot, null, secure: false), _f.Log));
        Assert.True(_f.Records.Has(RecordKind.ScheduledTask));
        Assert.False(_f.Records.Has(RecordKind.Shortcut));
        _f.Records.Remove(task);
        Assert.Empty(new ChangeRecords(_f.Paths, _f.Log).All());
    }

    [Fact]
    public void ARecordAnotherPoolWatchWroteIsKeptAndListed()
    {
        var app = new ChangeRecords(_f.Paths, _f.Log); // the tray app, read at its start
        var clearTask = ChangeRecordFixtures.ClearTask();
        _f.Records.Put(clearTask); // the command line, meanwhile

        var task = ChangeRecordFixtures.Task();
        app.Put(task); // with no listing in between
        Assert.True(app.Has(RecordKind.RemovedClearTask));
        Assert.Equal([clearTask, task], new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.Equal([clearTask, task], app.All());
    }

    [Fact]
    public void AFileThatCannotBeReadIsNeverWrittenOver()
    {
        _f.Records.Put(ChangeRecordFixtures.ClearTask());
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        string newer = File.ReadAllText(file).Replace("\"version\": 1", "\"version\": 2"); // a newer PoolWatch's list
        File.WriteAllText(file, newer);

        var records = new ChangeRecords(_f.Paths, _f.Log);
        var refused = Assert.Throws<InvalidDataException>(() => records.Put(ChangeRecordFixtures.Task()));
        Assert.Contains(file, refused.Message);
        Assert.Throws<InvalidDataException>(() => records.All());
        bool switched = false;
        string? notSwitched = records.Toggle(ChangeKind.ScheduledTask, @"\Vendor\Updater", "Updater", enable: false, "the Services tab", () => { switched = true; return null; });
        Assert.StartsWith("not switched, because PoolWatch could not save how it was set: PoolWatch cannot read its list of changes", notSwitched);
        Assert.False(switched); // never switched without its record
        records.Remove(ChangeRecordFixtures.ClearTask());
        Assert.Equal(newer, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    public void AnEmptyListIsNeverWrittenOver(string empty)
    {
        // PoolWatch never writes an empty list (it writes {"version": 1, "changes": []}); a power cut can leave one.
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        File.WriteAllText(file, empty);

        var records = new ChangeRecords(_f.Paths, _f.Log);
        Assert.Throws<InvalidDataException>(() => records.Put(ChangeRecordFixtures.Task()));
        var error = Assert.Throws<InvalidDataException>(() => records.All());
        Assert.Contains("it is empty, as a power cut can leave a file. The file is left as it is: restore it from a backup, or delete it", error.Message);
        Assert.Equal(empty, File.ReadAllText(file));
    }

    [Fact]
    public void ASaveLeavesNoTemporaryFile()
    {
        var task = ChangeRecordFixtures.Task();
        _f.Records.Put(task);
        _f.Records.Put(ChangeRecordFixtures.Startup());
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        Assert.Equal([task, ChangeRecordFixtures.Startup()], new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void AListThatIsALinkIsNeverReadThroughIt()
    {
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        string target = Directory.CreateDirectory(Path.Combine(_f.Root, "elsewhere")).FullName;
        Links.Junction(file, target);
        try
        {
            var records = new ChangeRecords(_f.Paths, _f.Log);
            Assert.Contains("is a link", Assert.Throws<UnauthorizedAccessException>(() => records.All()).Message); // not an empty list
            Assert.Throws<UnauthorizedAccessException>(() => records.Put(ChangeRecordFixtures.Task()));
        }
        finally { Links.DeleteLinkOnly(file); }
    }

    [Fact]
    public void AnImportFileThatIsALinkIsRefusedBeforeAnythingIsWritten()
    {
        var record = ChangeRecordFixtures.ClearTask(logonType: 1, user: @"PC\someone"); // one PoolWatch saves for import
        string file = _f.Records.ImportFile(record);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string target = Directory.CreateDirectory(Path.Combine(_f.Root, "elsewhere")).FullName;
        Links.Junction(file, target);
        try
        {
            Assert.Contains("is a link", Assert.Throws<UnauthorizedAccessException>(() => _f.Records.SaveForImport(record)).Message);
            Assert.False(File.Exists(file + ".tmp"));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally { Links.DeleteLinkOnly(file); }
    }

    [Fact]
    public void AListThatBecameALinkAfterItsPathWasCheckedIsRefusedByItsHandle()
    {
        // Read as if the path check had passed just before the link was put there: the handle is opened on the link itself.
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        string target = Directory.CreateDirectory(Path.Combine(_f.Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(target, "list.json"), ChangeRecordFile.ToJson([ChangeRecordFixtures.ClearTask()]));
        Links.Junction(file, target);
        try
        {
            var refused = Assert.Throws<UnauthorizedAccessException>(() => ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, file, ChangeRecordFile.MaxBytes));
            Assert.Contains("is a link", refused.Message); // not "is a folder": the handle is the link's own, not its target's
        }
        finally { Links.DeleteLinkOnly(file); }
    }

    [Fact]
    public void AListReachedThroughAFolderThatBecameALinkIsRefused()
    {
        // A folder below the data root that became a link after the path was checked: the open follows it, the handle says where it went.
        string target = Directory.CreateDirectory(Path.Combine(_f.Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(target, "applied-changes.json"), ChangeRecordFile.ToJson([ChangeRecordFixtures.ClearTask()]));
        string folder = Path.Combine(_f.Paths.DataRoot, "folder");
        Links.Junction(folder, target);
        try
        {
            var refused = Assert.Throws<UnauthorizedAccessException>(() =>
                ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, Path.Combine(folder, "applied-changes.json"), ChangeRecordFile.MaxBytes));
            Assert.Contains("is reached through a link", refused.Message);
        }
        finally { Links.DeleteLinkOnly(folder); }
    }

    [Fact]
    public void AListWhoseDataRootIsALinkIsRefused()
    {
        // The data root itself made a link (the root is judged at every start; this is the handle's own check).
        string real = Directory.CreateDirectory(Path.Combine(_f.Root, "real-root")).FullName;
        File.WriteAllText(Path.Combine(real, "applied-changes.json"), "{}");
        string root = Path.Combine(_f.Root, "linked-root");
        Links.Junction(root, real);
        try
        {
            var refused = Assert.Throws<UnauthorizedAccessException>(() => ChangeRecords.ReadPlainFile(root, Path.Combine(root, "applied-changes.json"), 100));
            Assert.Contains("is reached through a link", refused.Message);
        }
        finally { Links.DeleteLinkOnly(root); }
    }

    [Fact]
    public void ALinkAboveTheDataRootIsNotOneOfPoolWatchsOwn()
    {
        // A data root whose parent is a link (a moved ProgramData, say), which only an administrator can make: still read.
        string real = Directory.CreateDirectory(Path.Combine(_f.Root, "real", "data")).FullName;
        File.WriteAllText(Path.Combine(real, "applied-changes.json"), "{}");
        string above = Path.Combine(_f.Root, "above");
        Links.Junction(above, Path.GetDirectoryName(real)!);
        try { Assert.Equal("{}", ChangeRecords.ReadPlainFile(Path.Combine(above, "data"), Path.Combine(above, "data", "applied-changes.json"), 100)); }
        finally { Links.DeleteLinkOnly(above); }
    }

    [Fact]
    public void AListWithASecondNameIsRefused()
    {
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        File.WriteAllText(file, "{}");
        Links.HardLink(Path.Combine(_f.Root, "other-name.json"), file);
        var refused = Assert.Throws<UnauthorizedAccessException>(() => ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, file, 100));
        Assert.Contains("hard link", refused.Message);
    }

    [Fact]
    public void AFolderWhereTheListShouldBeIsRefusedAsAFolder()
    {
        string file = Directory.CreateDirectory(Path.Combine(_f.Paths.DataRoot, "applied-changes.json")).FullName;
        var refused = Assert.Throws<UnauthorizedAccessException>(() => ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, file, 100));
        Assert.Contains("is a folder", refused.Message);
        Assert.DoesNotContain("is a link", refused.Message);
    }

    [Fact]
    public void AListInAFolderThatIsNotThereIsNoList()
    {
        // No data folder yet: no list, so no changes (not a list that cannot be read).
        string missing = Path.Combine(_f.Root, "not-there");
        Assert.Throws<FileNotFoundException>(() => ChangeRecords.ReadPlainFile(missing, Path.Combine(missing, "applied-changes.json"), 100));
    }

    [Fact]
    public void TheSizeBoundIsOnTheBytesRead()
    {
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        File.WriteAllText(file, ChangeRecordFile.ToJson([ChangeRecordFixtures.ClearTask()]));
        int size = (int)new FileInfo(file).Length;
        Assert.Null(ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, file, size - 1)); // one byte over: not read
        Assert.Equal(File.ReadAllText(file), ChangeRecords.ReadPlainFile(_f.Paths.DataRoot, file, size));

        File.WriteAllBytes(file, new byte[ChangeRecordFile.MaxBytes + 1]);
        var refused = Assert.Throws<InvalidDataException>(() => new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.Contains("it is over 1 MB", refused.Message);
        Assert.Equal(ChangeRecordFile.MaxBytes + 1, new FileInfo(file).Length); // left as it is
    }

    [Fact]
    public void TheListIsInTheDataRootEvenWithAnOutputFolder()
    {
        // Undo acts on these records as an administrator: never from a headless --out folder.
        var paths = new AppPaths(_f.Paths.DataRoot, Path.Combine(_f.Root, "out"), secure: false);
        var records = new ChangeRecords(paths, _f.Log);
        records.Put(ChangeRecordFixtures.Task());
        records.PutRemovedTask(new CacheHelper.LegacyTaskInfo(@"\Clear standby", "someone", "RAMMap64.exe -Et", "1 trigger", true)
            { Xml = TaskXml.Ram(), LogonType = 1, UserId = @"PC\someone" }, "the command line"); // saved for import too

        Assert.Equal(2, new ChangeRecords(_f.Paths, _f.Log).All().Count);
        Assert.True(File.Exists(Path.Combine(_f.Paths.DataRoot, "removed-tasks", "Clear standby.xml")));
        Assert.Empty(Directory.GetFileSystemEntries(paths.OutDir!));
    }

    [Fact]
    public void AFailedSaveChangesNothing()
    {
        var task = ChangeRecordFixtures.Task();
        _f.Records.Put(task);
        string file = Path.Combine(_f.Paths.DataRoot, "applied-changes.json");
        string before = File.ReadAllText(file);
        Directory.CreateDirectory(file + ".tmp"); // the temporary file cannot be written

        Assert.Throws<UnauthorizedAccessException>(() => _f.Records.Put(ChangeRecordFixtures.Shortcut(@"C:\Users\someone\Desktop\x.lnk")));
        Assert.False(_f.Records.Has(RecordKind.Shortcut)); // before All, which reads the file again
        Assert.Equal([task], _f.Records.All());
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public void AnInvalidRecordIsRefusedAndNothingChanges()
    {
        _f.Records.Put(ChangeRecordFixtures.Task());
        string before = File.ReadAllText(Path.Combine(_f.Paths.DataRoot, "applied-changes.json"));
        Assert.Throws<ArgumentException>(() => _f.Records.Put(ChangeRecordFixtures.Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag")));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_f.Paths.DataRoot, "applied-changes.json")));
        Assert.Single(_f.Records.All());
    }

    /// <summary>The Services tab's switch of the fake task or startup entry, through the records as the tab makes it.</summary>
    private string? Switch(ChangeKind kind, bool enable, string? fails = null, Action? whileSwitching = null) =>
        kind == ChangeKind.ScheduledTask
            ? _f.Records.Toggle(kind, @"\Vendor\Updater", "Updater", enable, "the Services tab", () => { whileSwitching?.Invoke(); return fails ?? _f.Toggles.SetTaskEnabled(@"\Vendor\Updater", enable); })
            : _f.Records.Toggle(kind, "HKCU Run", "Helper", enable, "the Services tab", () => { whileSwitching?.Invoke(); return fails ?? _f.Toggles.SetStartupEnabled("HKCU Run", "Helper", enable); });

    [Fact]
    public void ASwitchIsRecordedOnceAndSwitchingBackRemovesIt()
    {
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false));
        var first = Assert.Single(_f.Records.All());
        Assert.Equal((RecordKind.ScheduledTask, true, @"\Vendor\Updater"), (first.Kind, first.EnabledBefore, first.Location));

        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false));
        Assert.Equal(first, Assert.Single(_f.Records.All())); // the first record, with the state before it, stays

        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: true));
        Assert.Empty(_f.Records.All()); // back as it was: nothing to undo

        Assert.Null(Switch(ChangeKind.StartupEntry, enable: true));
        var startup = Assert.Single(_f.Records.All());
        Assert.Equal((RecordKind.StartupEntry, false, "Helper"), (startup.Kind, startup.EnabledBefore, startup.Name));
        Assert.Throws<ArgumentException>(() => _f.Records.Toggle(ChangeKind.Service, "x", "y", true, "z", () => null));
    }

    [Fact]
    public void ASwitchIsRecordedBeforeItIsMadeAndTakenBackWhenItFails()
    {
        // Recorded first: the record is in the file while the switch is made, and is an Active changes row after it.
        int seen = -1;
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false, whileSwitching: () => seen = _f.Records.All().Count));
        Assert.Equal(1, seen);
        Assert.False(_f.Toggles.Tasks[@"\Vendor\Updater"]);
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.Equal((ChangeKind.ScheduledTask, @"Turned off the scheduled task \Vendor\Updater", true), (row.Kind, row.What, row.CanUndo));
        var first = Assert.Single(_f.Records.All());

        // Switching it back keeps its record until it is done, so a switch that fails (or a PoolWatch that stops in
        // between) leaves Undo knowing how it was.
        Assert.Equal("access denied", Switch(ChangeKind.ScheduledTask, enable: true, fails: "access denied", whileSwitching: () => seen = _f.Records.All().Count));
        Assert.Equal(1, seen);
        Assert.Equal(first, Assert.Single(_f.Records.All()));

        // A first switch that fails leaves no record of it, nor one that throws.
        Assert.Equal("access denied", Switch(ChangeKind.StartupEntry, enable: false, fails: "access denied"));
        Assert.Equal([first], _f.Records.All());
        Assert.Throws<InvalidOperationException>(() => Switch(ChangeKind.StartupEntry, enable: false, whileSwitching: () => throw new InvalidOperationException("COM")));
        Assert.Equal([first], _f.Records.All());

        // Another PoolWatch saved a record of its own while this switch failed: that record stays.
        var later = ChangeRecordFixtures.Startup() with { ChangedUtc = DateTime.UtcNow, Via = "the command line" };
        Assert.Equal("access denied", Switch(ChangeKind.StartupEntry, enable: false, fails: "access denied", whileSwitching: () => _f.Records.Put(later)));
        Assert.Contains(later, _f.Records.All());
    }

    [Fact]
    public void SwitchingBackRemovesOnlyTheRecordItFoundAndOnlyOnceItIsDone()
    {
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false));
        int seen = -1;
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: true, whileSwitching: () => seen = _f.Records.All().Count));
        Assert.Equal(1, seen); // still recorded while it is switched back
        Assert.Empty(_f.Records.All());
        Assert.True(_f.Toggles.Tasks[@"\Vendor\Updater"]);

        // Another PoolWatch recorded that task again while it was switched back: its record is its own to keep.
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false));
        var again = Assert.Single(_f.Records.All()) with { ChangedUtc = DateTime.UtcNow.AddMinutes(1), Via = "the command line" };
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: true, whileSwitching: () => _f.Records.Put(again)));
        Assert.Equal([again], _f.Records.All());
    }

    [Fact]
    public void TheServicesTabsSwitchIsAnActiveChangesRowBeforeItIsMade()
    {
        // The Disable / Enable buttons' own call, with the row's tag as the tab lists it.
        var task = new ScheduledTaskView(@"\Vendor\Updater", "Updater", "Vendor", Enabled: true, "updater.exe", "");
        var startup = new StartupView("Helper", "helper.exe", "HKCU Run", Enabled: false, Microsoft: false);
        int rows = -1;
        string? SetTask(string path, bool enable) { rows = _f.Recorded(_f.Live()).List().Count(); return _f.Toggles.SetTaskEnabled(path, enable); }

        Assert.Null(Ui.ServicesPageText.Switch(task, enable: false, _f.Records, SetTask, _f.Toggles.SetStartupEnabled));
        Assert.Equal(1, rows); // already a row while it is switched
        Assert.Equal(["task \\Vendor\\Updater False"], _f.Toggles.Calls);
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.Equal((ChangeKind.ScheduledTask, @"Turned off the scheduled task \Vendor\Updater", true), (row.Kind, row.What, row.CanUndo));

        Assert.Null(Ui.ServicesPageText.Switch(startup, enable: true, _f.Records, SetTask, _f.Toggles.SetStartupEnabled));
        Assert.Equal(2, _f.Recorded(_f.Live()).List().Count());
        Assert.True(_f.Toggles.Startup[@"HKCU Run\Helper"]);

        Assert.Equal("select a scheduled task or startup app", Ui.ServicesPageText.Switch("an app", enable: true, _f.Records, SetTask, _f.Toggles.SetStartupEnabled));
        Assert.Equal(2, _f.Toggles.Calls.Count);
    }

    [Fact]
    public void ASwitchPoolWatchCannotRecordIsNotMadeAndSaysWhyOnce()
    {
        bool switched = false;
        string? refused = _f.Records.Toggle(ChangeKind.ScheduledTask, @"\Microsoft\Windows\Defrag\ScheduledDefrag", "ScheduledDefrag", enable: false,
            "the Services tab", () => { switched = true; return null; });
        Assert.Equal("not switched, because PoolWatch cannot record it to put it back later: not a scheduled task PoolWatch may change", refused);
        Assert.False(switched);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void RemovedClearTasksAreRecordedOneByOne()
    {
        var good = new CacheHelper.LegacyTaskInfo(@"\Clear standby", "SYSTEM", "RAMMap64.exe -Et", "1 trigger", true) { Xml = TaskXml.Ram(), LogonType = 5, UserId = "S-1-5-18" };
        var unreadable = good with { Path = @"\Clear modified", Xml = null };
        _f.Records.RecordRemovedClearTasks([unreadable, good], "the tray menu");
        var r = Assert.Single(_f.Records.All());
        Assert.Equal((RecordKind.RemovedClearTask, @"\Clear standby", "the tray menu", 5), (r.Kind, r.Name, r.Via, r.TaskLogonType));
        Assert.Equal(good.Xml, r.TaskXml);
    }

    [Theory]
    [InlineData(@"\Clear standby", "Clear standby.xml")]
    [InlineData(@"\a:b|c", "a_b_c.xml")]
    [InlineData(@"\CON", "_CON.xml")]
    [InlineData(@"\lpt1", "_lpt1.xml")]
    public void ImportFilesHaveSafeNames(string task, string file) => Assert.Equal(file, ChangeRecords.ImportFileName(task));

    // ---------------------------------------------------------------- recorded changes: switches and shortcuts

    [Fact]
    public void ATaskIsSwitchedBackAndItsRecordGoes()
    {
        _f.Toggles.Tasks[@"\Vendor\Updater"] = false;
        _f.Records.Put(ChangeRecordFixtures.Task());
        var source = _f.Recorded(_f.Live());
        var row = Assert.Single(source.List());
        Assert.Equal((ChangeKind.ScheduledTask, @"Turned off the scheduled task \Vendor\Updater", true), (row.Kind, row.What, row.CanUndo));

        var result = source.Undo(row, confirmed: false);
        Assert.True(result.Done);
        Assert.Equal(@"Turned the scheduled task \Vendor\Updater back on.", result.Message);
        Assert.True(_f.Toggles.Tasks[@"\Vendor\Updater"]);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void AGoneTargetLosesItsRecordAndAFailureKeepsIt()
    {
        _f.Records.Put(ChangeRecordFixtures.Startup());
        var source = _f.Recorded(_f.Live());
        var row = Assert.Single(source.List());
        var gone = source.Undo(row, false); // not in the fake: deleted since
        Assert.True(gone.Done);
        Assert.Contains("is no longer there", gone.Message);
        Assert.Empty(_f.Toggles.Calls);
        Assert.Empty(_f.Records.All());

        _f.Records.Put(ChangeRecordFixtures.Startup());
        _f.Toggles.Startup[@"HKCU Run\Helper"] = false;
        _f.Toggles.Error = "access denied";
        var failed = source.Undo(row, false);
        Assert.False(failed.Done);
        Assert.Contains("access denied", failed.Message);
        Assert.Single(_f.Records.All());

        _f.Records.Put(ChangeRecordFixtures.Task());
        _f.Toggles.Unreadable = true; // cannot tell whether it is gone: nothing is removed
        var unknown = source.Undo(source.List().Single(c => c.Kind == ChangeKind.ScheduledTask), false);
        Assert.False(unknown.Done);
        Assert.Equal(2, _f.Records.All().Count);
    }

    [Fact]
    public void ASwitchBackRemovesOnlyTheRecordItPutBack()
    {
        _f.Toggles.Tasks[@"\Vendor\Updater"] = false;
        _f.Records.Put(ChangeRecordFixtures.Task());
        var source = _f.Recorded(_f.Live());
        // While Undo switches it back, another PoolWatch saves a record of its own for that task.
        var later = ChangeRecordFixtures.Task() with { ChangedUtc = DateTime.UtcNow, Via = "the command line" };
        _f.Toggles.OnSet = () => _f.Records.Put(later);

        Assert.True(source.Undo(Assert.Single(source.List()), confirmed: false).Done);
        Assert.Equal("the command line", Assert.Single(_f.Records.All()).Via);
    }

    [Fact]
    public async Task AnUndoAndTheServicesTabsSwitchOfATaskRunOneAtATime()
    {
        // Undo (a worker in Active changes) turns the task back on; while it does, Disable is clicked on the Services tab
        // (the UI thread). Interleaved, the click would keep the record Undo then removes, and leave the task off with no
        // record of it.
        _f.Toggles.Tasks[@"\Vendor\Updater"] = false;
        _f.Records.Put(ChangeRecordFixtures.Task());
        var source = _f.Recorded(_f.Live());
        Task<string?>? click = null;
        bool clickWaited = false;
        _f.Toggles.OnSet = () =>
        {
            click = Task.Run(() => Switch(ChangeKind.ScheduledTask, enable: false));
            clickWaited = !click.Wait(300);
        };

        Assert.True(source.Undo(Assert.Single(source.List()), confirmed: false).Done);
        Assert.Null(await click!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(clickWaited); // until the Undo was done
        // So the click came after it: the task is off again, with a record of its own for Undo.
        Assert.False(_f.Toggles.Tasks[@"\Vendor\Updater"]);
        var record = Assert.Single(_f.Records.All());
        Assert.NotEqual(ChangeRecordFixtures.Task(), record);
        Assert.Equal((true, "the Services tab"), (record.EnabledBefore, record.Via));
    }

    [Fact]
    public async Task AnUndoWaitsWhileTheServicesTabsSwitchOfATaskIsMade()
    {
        // The other order: the tab's Disable is in flight (the task is off already: the same way again, so its record
        // stays) when Undo starts on a worker. Undo must wait; else it turns the task on and removes the record, and the
        // tab's switch then lands, leaving the task off with no record of it.
        _f.Toggles.Tasks[@"\Vendor\Updater"] = false;
        _f.Records.Put(ChangeRecordFixtures.Task());
        var source = _f.Recorded(_f.Live());
        var row = Assert.Single(source.List());
        Task<UndoResult>? undo = null;
        bool undoWaited = false;
        Assert.Null(Switch(ChangeKind.ScheduledTask, enable: false, whileSwitching: () =>
        {
            undo = Task.Run(() => source.Undo(row, confirmed: false));
            undoWaited = !undo.Wait(300);
        }));
        Assert.True((await undo!.WaitAsync(TimeSpan.FromSeconds(10))).Done);
        Assert.True(undoWaited); // until the tab's switch was done
        // So Undo came after it: the task is back on, and nothing is left to undo.
        Assert.True(_f.Toggles.Tasks[@"\Vendor\Updater"]);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void ATaskThatIsNoLongerThereLosesOnlyTheRecordItActedOn()
    {
        _f.Records.Put(ChangeRecordFixtures.Task()); // the task is not in the fake: deleted since
        var source = _f.Recorded(_f.Live());
        // While Undo looks for it, another PoolWatch saves a record of its own for that task.
        var later = ChangeRecordFixtures.Task() with { ChangedUtc = DateTime.UtcNow, Via = "the command line" };
        _f.Toggles.OnExists = () => _f.Records.Put(later);

        var result = source.Undo(Assert.Single(source.List()), confirmed: false);
        Assert.True(result.Done);
        Assert.Contains("is no longer there", result.Message);
        Assert.Equal(later, Assert.Single(_f.Records.All()));
    }

    /// <summary>
    /// Your Desktop for a shortcut's Undo, which lets another PoolWatch save a record of its own when Undo first reads it,
    /// as if that save happened while the Undo ran.
    /// </summary>
    private sealed class DesktopSavedMeanwhile(string desktop, Action save) : IReadOnlyList<string>
    {
        private Action? _save = save;
        private string Read() { if (_save is { } s) { _save = null; s(); } return desktop; }
        public string this[int index] => index == 0 ? Read() : throw new ArgumentOutOfRangeException(nameof(index));
        public int Count => 1;
        public IEnumerator<string> GetEnumerator() { yield return Read(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed since")]
    [InlineData("gone")]
    public void AShortcutUndoRemovesOnlyTheRecordItActedOn(string how)
    {
        var (desktop, file) = Shortcut(how == "changed since" ? [9, 9, 9] : [1, 2, 3, 4], recorded: [1, 2, 3, 4]);
        if (how == "gone") File.Delete(file);
        var later = ChangeRecordFixtures.Shortcut(file) with { ChangedUtc = DateTime.UtcNow, Via = "the command line" };
        var source = new RecordedSource(_f.Records, _f.Toggles, _f.ClearTasks, _f.Live(), new DesktopSavedMeanwhile(desktop, () => _f.Records.Put(later)));

        var result = source.Undo(Assert.Single(source.List()), confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.Equal(how == "changed since", File.Exists(file)); // deleted only when it was still exactly what PoolWatch wrote
        Assert.Equal(later, Assert.Single(_f.Records.All()));
    }

    private (string Desktop, string File) Shortcut(byte[] bytes, byte[]? recorded = null)
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_f.Root, "Desktop")).FullName;
        string file = Path.Combine(desktop, "ChatGPT (no shader cache).lnk");
        File.WriteAllBytes(file, bytes);
        _f.Records.Put(ChangeRecordFixtures.Shortcut(file) with { Sha256 = Convert.ToHexString(SHA256.HashData(recorded ?? bytes)) });
        return (desktop, file);
    }

    [Fact]
    public void TheShortcutIsDeletedWhenItIsUnchanged()
    {
        var (desktop, file) = Shortcut([1, 2, 3, 4]);
        var source = _f.Recorded(_f.Live(), desktop);
        var row = Assert.Single(source.List());
        Assert.Equal("Created the desktop shortcut \"ChatGPT (no shader cache)\"", row.What);
        var result = source.Undo(row, false);
        Assert.True(result.Done, result.Message);
        Assert.False(File.Exists(file));
        Assert.Empty(_f.Records.All());
        Assert.Equal("This change is already put back.", source.Undo(row, false).Message);
    }

    [Fact]
    public void AChangedShortcutIsLeftInPlace()
    {
        var (desktop, file) = Shortcut([9, 9, 9], recorded: [1, 2, 3, 4]);
        var source = _f.Recorded(_f.Live(), desktop);
        var result = source.Undo(Assert.Single(source.List()), false);
        Assert.True(result.Done);
        Assert.Contains("was changed after PoolWatch made it", result.Message);
        Assert.True(File.Exists(file));
        Assert.Empty(_f.Records.All()); // it is yours now
    }

    [Fact]
    public void AShortcutOutsideTheDesktopIsNeverDeleted()
    {
        var (_, file) = Shortcut([1, 2, 3, 4]);
        var source = _f.Recorded(_f.Live(), Path.Combine(_f.Root, "Elsewhere"));
        var result = source.Undo(Assert.Single(source.List()), false);
        Assert.False(result.Done);
        Assert.True(File.Exists(file));
        Assert.Single(_f.Records.All());
    }

    [Fact]
    public void AMissingShortcutLosesItsRecord()
    {
        var (desktop, file) = Shortcut([1, 2, 3, 4]);
        File.Delete(file);
        var source = _f.Recorded(_f.Live(), desktop);
        var result = source.Undo(Assert.Single(source.List()), false);
        Assert.True(result.Done);
        Assert.Contains("no longer there", result.Message);
        Assert.Empty(_f.Records.All());
    }

    // ---------------------------------------------------------------- settings, the logon task, services

    [Fact]
    public void SettingsAreListedWhileOnAndTurnedOffAtOnce()
    {
        var live = _f.Live(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1, AutoClearPoolGb = 8, TokenGuardEnabled = true });
        bool guardStopped = false;
        var source = new SettingsSource(live, _f.Records, () => guardStopped = true);
        var rows = source.List().ToList();
        Assert.Equal(["The cache is cleared every hour, or sooner when the kernel pool reaches 8 GB", ChangeWords.TokenGuard], rows.Select(r => r.What));
        Assert.Null(rows[0].Note);
        Assert.Equal(ChangeWords.TokenGuardNote, rows[1].Note);

        _f.Records.Put(ChangeRecordFixtures.ClearTask());
        Assert.Equal(ChangeWords.AutoClearKeepsTaskNote, source.List().First().Note);

        Assert.True(source.Undo(rows[0], false).Done);
        Assert.False(live.Running.AutoClearEnabled);
        Assert.False(_f.Saved().AutoClearEnabled);
        Assert.True(source.Undo(rows[1], false).Done);
        Assert.False(live.Running.TokenGuardEnabled);
        Assert.True(guardStopped);
        Assert.Empty(source.List());
    }

    private sealed class FakeLogonTasks : ILogonTasks
    {
        public LogonTaskPolicy.ExistingTask? Task = new(@"C:\Program Files\PoolWatch\PoolWatch.exe", "--tray", Highest: true);
        public string? RemoveError;
        public LogonTaskPolicy.ExistingTask? Read() => Task;
        public string? RegisterCurrent() => throw new InvalidOperationException("never registered here");
        public string? Remove() { if (RemoveError is null) Task = null; return RemoveError; }
    }

    [Fact]
    public void TheLogonTaskIsListedWhileItExists()
    {
        var tasks = new FakeLogonTasks { RemoveError = "access denied" };
        var source = new LogonTaskSource(tasks);
        var row = Assert.Single(source.List());
        Assert.Equal((ChangeIds.LogonTask, ChangeWords.LogonTask, (DateTime?)null), (row.Id, row.What, row.ChangedUtc));
        Assert.False(source.Undo(row, false).Done);
        tasks.RemoveError = null;
        Assert.Equal("PoolWatch no longer starts when you sign in.", source.Undo(row, false).Message);
        Assert.Empty(source.List());
    }

    /// <summary>
    /// The live services in memory. As Windows does, a service is not stopped while one that depends on it runs (its start
    /// type is set all the same), and not started while a service it needs is Disabled (a stopped one it needs starts with it).
    /// </summary>
    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MapsBroker"] = new ServiceView { Name = "MapsBroker", DisplayName = "Downloaded Maps Manager", StartType = ServiceStartType.Automatic, Running = true, MicrosoftSigned = true },
        };

        public IReadOnlyList<ServiceView> Enumerate() => Live.Values.ToList();
        public bool InsiderEnrolled() => false;
        public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            string name = op.Option.Service.Name;
            bool refused = op.NewStart == ServiceStartType.Disabled && Live[name].Running
                           && Live.Values.Any(v => v.Running && v.DependsOn.Contains(name, StringComparer.OrdinalIgnoreCase));
            Live[name] = Live[name] with { StartType = op.NewStart, Running = refused };
            return (null, refused ? ServiceControl.Answered(1051, lookedAgain: false).Why : null); // as Windows answers: one that depends on it runs
        }
        public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            bool running = change.OriginalRunning;
            NotDone? notStarted = null;
            var needs = Live[change.Service].DependsOn;
            if (running && needs.Any(d => Live.TryGetValue(d, out var v) && v.StartType == ServiceStartType.Disabled))
                (running, notStarted) = (false, ServiceControl.Started(1068)); // as Windows answers: one it needs did not start
            else if (running)
                foreach (var d in needs.Where(Live.ContainsKey)) Live[d] = Live[d] with { Running = true };
            Live[change.Service] = Live[change.Service] with { StartType = change.OriginalStart, Running = running };
            return (null, notStarted);
        }
        public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => null;
    }

    [Fact]
    public void AServiceIsListedFromItsOwnStoreAndRestored()
    {
        var backend = new FakeServices();
        var services = new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, backend);
        services.Apply(["MapsBroker"], "a suggestion");
        var source = new ServicesSource(services);
        var row = Assert.Single(source.List());
        Assert.Equal((ChangeIds.Service("MapsBroker"), "Turned off Downloaded Maps Manager (MapsBroker)"), (row.Id, row.What));
        Assert.NotNull(row.ChangedUtc);

        var result = source.Undo(row, false);
        Assert.True(result.Done, result.Message);
        Assert.Equal("Downloaded Maps Manager (MapsBroker) is back to how it was.", result.Message);
        Assert.Equal(ServiceStartType.Automatic, backend.Live["MapsBroker"].StartType);
        Assert.Empty(source.List());
        Assert.True(source.Undo(row, false).Done); // already back
    }

    /// <summary>
    /// On a PC at Windows defaults, "Agentic coding PC" ticks Xbox Live, whose services Windows ships at Manual, and Turn off
    /// sets them to Disabled and stops them, as it does Xbox accessories, ticked by hand afterwards (the tab's Via is then
    /// "manual"). Put all back in Active changes puts each back as Windows had it: Manual, and running again if it ran.
    /// </summary>
    [Fact]
    public void PutAllBackReturnsServicesTurnedOffAtTheirManualWindowsDefaultToManual()
    {
        string[] manualByDefault = ["XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc"];
        var backend = new FakeServices();
        foreach (var name in manualByDefault)
            backend.Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = ServiceStartType.Manual, Running = name == "XblAuthManager", MicrosoftSigned = true };
        var services = new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, backend);

        var ticked = ServicePlan.Tick(Preset.AgenticCodingPc, services.Options());
        Assert.Equal(["MapsBroker", "XblAuthManager", "XblGameSave", "XboxNetApiSvc"], ticked.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(services.Apply([.. ticked], ServiceProfileRules.AgenticCodingPc), r => Assert.Null(r.error));
        Assert.Null(Assert.Single(services.Apply(["XboxGipSvc"], "manual")).error);
        foreach (var name in manualByDefault)
        {
            Assert.Equal((ServiceStartType.Disabled, false), (backend.Live[name].StartType, backend.Live[name].Running));
            var record = services.Changes.Single(c => c.Service == name);
            Assert.Equal((ServiceStartType.Manual, name == "XboxGipSvc" ? "manual" : ServiceProfileRules.AgenticCodingPc), (record.OriginalStart, record.Via));
        }

        var results = Ledger(new ServicesSource(services)).UndoAll();
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Result.Done, r.Result.Message));
        foreach (var name in manualByDefault)
            Assert.Equal((ServiceStartType.Manual, name == "XblAuthManager"), (backend.Live[name].StartType, backend.Live[name].Running));
        Assert.Equal((ServiceStartType.Automatic, true), (backend.Live["MapsBroker"].StartType, backend.Live["MapsBroker"].Running));
        Assert.Empty(new ServicesSource(services).List());
    }

    /// <summary>
    /// Another app's service, Acme Maps, depends on MapsBroker, and both run. Turned off together, Acme Maps is stopped
    /// first, so both stop. Put all back in Active changes then puts MapsBroker back first, so both run again: Windows
    /// does not start a service while one it needs is Disabled.
    /// </summary>
    [Fact]
    public void PutAllBackPutsAServiceBackBeforeOneThatDependsOnIt()
    {
        var backend = new FakeServices();
        backend.Live["AcmeMaps"] = new ServiceView
        {
            Name = "AcmeMaps", DisplayName = "Acme Maps", StartType = ServiceStartType.Automatic, Running = true, DependsOn = ["RpcSs", "MapsBroker"],
        };
        var services = new ServicesController(_f.Paths, _f.Log, _ => { }, _ => null, _ => null, backend);
        Assert.All(services.Apply(["MapsBroker", "AcmeMaps"], "manual"), r => Assert.Null(r.error));
        Assert.Equal(["AcmeMaps", "MapsBroker"], services.Changes.Select(c => c.Service)); // stopped in that order

        var results = Ledger(new ServicesSource(services)).UndoAll();
        Assert.Equal(["Downloaded Maps Manager (MapsBroker) is back to how it was.", "Acme Maps (AcmeMaps) is back to how it was."],
            results.Select(r => r.Result.Message));
        foreach (var name in new[] { "MapsBroker", "AcmeMaps" })
            Assert.Equal((ServiceStartType.Automatic, true), (backend.Live[name].StartType, backend.Live[name].Running));
    }

    // ---------------------------------------------------------------- a source's own order for Put all back

    /// <summary>A source whose rows of one kind Put all back runs in the order it gives, unless that order is wrong or fails.</summary>
    private sealed class OrderedSource(List<string> calls, Func<IReadOnlyList<ActiveChange>, IReadOnlyList<ActiveChange>> order, params ActiveChange[] rows) : IChangeSource
    {
        public IEnumerable<ActiveChange> List() => rows;
        public UndoResult Undo(ActiveChange change, bool confirmed) { calls.Add(change.Id); return new(true, "undone " + change.Id); }
        public ActiveChange Unread(string error) => new("unread", ChangeKind.Service, null, "not read", false, error);
        public IReadOnlyList<ActiveChange> PutBackOrder(IReadOnlyList<ActiveChange> rows) => order(rows);
    }

    private static ActiveChange[] ThreeServices() =>
        new[] { "a", "b", "c" }.Select(id =>new ActiveChange("service:" + id, ChangeKind.Service, null, "the service " + id, true, null)).ToArray();

    /// <summary>The source's own order is used when it holds exactly the rows it was given.</summary>
    [Fact]
    public void PutAllBackRunsASourcesRowsInTheOrderItGives()
    {
        var calls = new List<string>();
        var results = Ledger(new OrderedSource(calls, rows => rows.Reverse().ToList(), ThreeServices())).UndoAll();
        Assert.Equal(["service:c", "service:b", "service:a"], calls);
        Assert.All(results, r => Assert.True(r.Result.Done));
    }

    /// <summary>
    /// An order that leaves a row out, that names one twice in another's place, or that throws: every row is put back
    /// all the same, in the order listed, and none twice.
    /// </summary>
    [Theory]
    [InlineData("drops a row")]
    [InlineData("names one twice")]
    [InlineData("throws")]
    public void PutAllBackRunsEveryRowInTheListedOrderWhenTheSourcesOrderIsWrongOrFails(string how)
    {
        var calls = new List<string>();
        Func<IReadOnlyList<ActiveChange>, IReadOnlyList<ActiveChange>> order = how switch
        {
            "drops a row" => rows => rows.Skip(1).Reverse().ToList(),
            "names one twice" => rows => [rows[2], rows[2], rows[0]],
            _ => _ => throw new InvalidOperationException("the services could not be read"),
        };
        var results = Ledger(new OrderedSource(calls, order, ThreeServices())).UndoAll();
        Assert.Equal(["service:a", "service:b", "service:c"], calls);
        Assert.Equal(["service:a", "service:b", "service:c"], results.Select(r => r.Change.Id));
        Assert.All(results, r => Assert.True(r.Result.Done));
    }
}

/// <summary>Records for the app tests (the Core tests have their own).</summary>
internal static class ChangeRecordFixtures
{
    private static readonly DateTime When = new(2026, 10, 28, 9, 12, 0, DateTimeKind.Utc);

    public static ChangeRecord Task(string path = @"\Vendor\Updater") => new()
    {
        Id = ChangeIds.ScheduledTask(path), Kind = RecordKind.ScheduledTask, ChangedUtc = When, Via = "the Services tab", Name = "Updater", Location = path, EnabledBefore = true,
    };

    public static ChangeRecord Startup() => new()
    {
        Id = ChangeIds.StartupEntry("HKCU Run", "Helper"), Kind = RecordKind.StartupEntry, ChangedUtc = When, Via = "the Services tab", Name = "Helper",
        Location = "HKCU Run", EnabledBefore = true,
    };

    public static ChangeRecord Shortcut(string path) => new()
    {
        Id = ChangeIds.Shortcut(path), Kind = RecordKind.Shortcut, ChangedUtc = When, Via = "a suggestion", Name = Path.GetFileName(path), Path = path, Sha256 = new string('0', 64),
    };

    public static ChangeRecord ClearTask(string path = @"\Clear standby", string? xml = null, int logonType = 5, string? user = "S-1-5-18") => new()
    {
        Id = ChangeIds.RemovedClearTask(path), Kind = RecordKind.RemovedClearTask, ChangedUtc = When, Via = "the setup guide", Name = path,
        TaskXml = xml ?? TaskXml.Ram(), TaskLogonType = logonType, TaskUserId = user,
    };
}

/// <summary>Task definitions as Task Scheduler gives them.</summary>
internal static class TaskXml
{
    public const string AdminRam = @"C:\Program Files\Sysinternals\RAMMap64.exe";

    public static string Ram(string program = AdminRam, string arguments = "-Et") => Of($"<Exec><Command>{program}</Command><Arguments>{arguments}</Arguments></Exec>");

    public static string Of(string actions, string head = "") => $"""
        <?xml version="1.0" encoding="UTF-16"?>{head}
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Actions Context="Author">{actions}</Actions>
        </Task>
        """;
}
