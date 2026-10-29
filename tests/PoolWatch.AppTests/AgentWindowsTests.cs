using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Services.Agent;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>Answers the AI-agent windows' dialogs and questions in memory: no dialog is shown and no file is touched.</summary>
internal sealed class FakeAgentFiles : IAgentFiles
{
    public string? SavePath;
    public readonly List<string> SaveNames = new();
    public readonly Dictionary<string, byte[]> Written = new();
    public string? WriteError;
    public byte[]? Answer;
    public string? ReadError;
    public readonly List<(string Path, int Max)> Reads = new();
    public string? Copied, CopyError;
    public bool Yes;
    public readonly List<string> Questions = new(), Told = new();

    public string? AskSavePath(IWin32Window owner, string defaultName) { SaveNames.Add(defaultName); return SavePath; }
    public string? AskOpenPath(IWin32Window owner) => null;

    public string? Write(string path, byte[] bytes)
    {
        if (WriteError is null) Written[path] = bytes;
        return WriteError;
    }

    public (byte[]? Bytes, string? Error) Read(string path, int max)
    {
        Reads.Add((path, max));
        return ReadError is { } e ? (null, e) : (Answer, null);
    }

    public string? Copy(string text) { if (CopyError is null) Copied = text; return CopyError; }
    public bool AskYesNo(IWin32Window owner, string question, string title) { Questions.Add(question); return Yes; }
    public void Tell(IWin32Window owner, string message, string title) => Told.Add(message);
}

/// <summary>Runs a window's background work in line, so the test sees its result at once.</summary>
internal sealed class InlineWork : IAgentWork
{
    public void Run<T>(Control window, Func<T> work, Action<T> done, Action<Exception> failed)
    {
        T result;
        try { result = work(); }
        catch (Exception ex) { failed(ex); return; }
        done(result);
    }
}

/// <summary>The service control manager, in memory: every change is recorded and nothing on the PC is read or changed.</summary>
internal sealed class AgentFakeServices : IServiceBackend
{
    public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = new();

    /// <summary>How many times the services were read.</summary>
    public int Reads;

    /// <summary>Thrown when the services are read (the service control manager not answering): every time, or only at read <see cref="FailOnRead"/>.</summary>
    public Exception? Fail;

    public int? FailOnRead;

    public void Add(string name, string display, ServiceStartType start, bool running, bool microsoft = true, string? publisher = null) =>
        Live[name] = new ServiceView { Name = name, DisplayName = display, StartType = start, Running = running, MicrosoftSigned = microsoft, Publisher = publisher };

    public IReadOnlyList<ServiceView> Enumerate()
    {
        Reads++;
        if (Fail is { } fail && (FailOnRead is null || FailOnRead == Reads)) throw fail;
        return Live.Values.ToList();
    }
    /// <summary>Whether this made-up PC is enrolled in Windows Insider, as the backend reads it (never this host's own flag).</summary>
    public bool Insider;

    public bool InsiderEnrolled() => Insider;

    public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        Calls.Add("apply " + op.Option.Service.Name);
        Live[op.Option.Service.Name] = Live[op.Option.Service.Name] with { StartType = op.NewStart, Running = false };
        return (null, null);
    }

    public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        Calls.Add("restore " + change.Service);
        return (null, null);
    }

    public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        Calls.Add("default " + op.Option.Service.Name);
        return null;
    }
}

/// <summary>
/// A made-up PC for the AI-agent windows (its data root in TEMP, no ACL touched): services in a fake service manager
/// (a catalogue service running, one off already, a protected one and two of other apps), startup apps and scheduled
/// tasks as the Tasks and startup apps tab reads them, and the switches in memory (<see cref="FakeToggles"/>).
/// </summary>
internal sealed class AgentPc : IDisposable
{
    public const string User = "jdoe-example", Machine = "EXAMPLE-PC4";

    public AgentPc()
    {
        Backend.Add("MapsBroker", "Downloaded Maps Manager", ServiceStartType.Automatic, running: true);
        Backend.Add("Fax", "Fax", ServiceStartType.Disabled, running: false);
        Backend.Add("WinDefend", "Microsoft Defender Antivirus Service", ServiceStartType.Automatic, running: true);
        Backend.Add("ExampleVendorSvc", "Example Device Service", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Devices Ltd");
        Backend.Add("OtherVendorSvc", "Other Example Service", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Other Example Ltd");
        Services = new ServicesController(Changes.Paths, Changes.Log, Findings.Add, _ => null, _ => null, Backend);
    }

    public ChangeFixture Changes { get; } = new();
    public AgentFakeServices Backend { get; } = new();
    public ServicesController Services { get; }
    public List<PoolWatch.Core.Findings.Finding> Findings { get; } = new();
    public List<(string What, string? Name)> Timeline { get; } = new();

    public List<StartupView> Startup { get; } =
    [
        new("Example Deck", "\"C:\\Program Files\\Example\\Deck\\deck.exe\" --tray --user " + User, "HKCU Run", Enabled: true, Microsoft: false),
        new("Old Launcher.lnk", @"C:\Program Files\Old Example\launcher.exe", "Startup folder (you)", Enabled: false, Microsoft: false),
        new("OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe /background", "HKCU Run", Enabled: true, Microsoft: true),
    ];

    public List<ScheduledTaskView> Tasks { get; } =
    [
        new(@"\ExampleVendor\Example Power Notice", "Example Power Notice", "Example Devices Ltd", Enabled: true, @"C:\Program Files\ExampleVendor\notice.exe", ""),
        new(@"\Example Updater", "Example Updater", "", Enabled: false, @"C:\Program Files\Example\updater.exe", ""),
    ];

    public static readonly RedactionInfo Redaction = new(User, Machine, [@"C:\Users\" + User]);

    /// <summary>The list as the app builds it for this PC, with the deck's program running at 0.2 core.</summary>
    public AgentItemList Items() => AgentPcReader.Build(Services.Options(), Startup, Tasks,
        [new LiveImage(new ProcessKey(4242, 1), "deck.exe", @"C:\Program Files\Example\Deck\deck.exe")],
        k => k.Pid == 4242 ? (0.2, 9000) : null, insiderEnrolled: false, Redaction, s => s);

    /// <summary>An answer that names something of every kind: to turn off, off already, refused, kept and not here.</summary>
    public const string Answer = """
        { "version": 1,
          "turnOff": { "services": ["MapsBroker", "ExampleVendorSvc", "Fax", "WinDefend", "NoSuchService"],
                       "startup": ["Example Deck", "Old Launcher.lnk", "OneDrive"],
                       "tasks": ["Example Power Notice"] },
          "keep": { "services": ["OtherVendorSvc", "NotHere"] },
          "why": { "MapsBroker": "You said you never use offline maps.", "Example Deck": "The deck is not plugged in & you said so." } }
        """;

    public AgentPlan Plan(string json = Answer)
    {
        var read = AgentAnswer.Read(json, Items());
        Assert.True(read.Error is null, read.Error);
        return read.Plan!;
    }

    /// <summary>Apply as the app runs it: through the services controller and the records, with the timeline told.</summary>
    public List<(string Name, string? Error)> Apply(AgentPlan plan, IReadOnlyCollection<string> services) =>
        AgentApply.Run(plan, services, Services, Changes.Records, Changes.Toggles, (what, name) => Timeline.Add((what, name)));

    /// <summary>Active changes as the app lists it: the services' record and the switches' records.</summary>
    public IReadOnlyList<ActiveChange> ActiveChanges() =>
        new ChangeLedger([new ServicesSource(Services), Changes.Recorded(Changes.Live())], Changes.Records, Changes.Log).List();

    public void Dispose() => Changes.Dispose();
}

/// <summary>
/// The "Decide with your AI agent" window and the review of an imported answer, without showing either: what the prompt
/// window shows and saves, the review's parts (what Apply turns off, what it refuses and why, what is left as it is),
/// Cancel changing nothing, Apply going through the paths that record each change first (so each is under Active changes
/// with the answer as where it came from), the files read and written without following a link, and the Services tab's
/// two buttons opening the windows.
/// </summary>
public sealed class AgentWindowsTests : IDisposable
{
    private readonly AgentPc _pc = new();
    private readonly FakeAgentFiles _files = new();

    public void Dispose() => _pc.Dispose();

    /// <summary>As a click does (PerformClick needs the window on screen).</summary>
    private static void Click(Button b) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(b, [EventArgs.Empty]);

    private const string Windows = "Windows 11 Pro 25H2 (build 26220.9757)";

    private string Prompt() => AgentPrompt.Build(_pc.Items(), Windows);

    // ---------------------------------------------------------------- the prompt window's model

    [Fact]
    public void TheModelHasNoPromptUntilItIsBuiltAndSavesItAsPoolwatchServicesMd()
    {
        var model = new AgentPromptModel();
        Assert.False(model.Ready);
        Assert.Equal(AgentPromptText.Reading, model.Preview);
        Assert.Equal("poolwatch-services.md", model.FileName);

        string prompt = Prompt();
        model.Built(prompt);
        Assert.True(model.Ready);
        Assert.Equal(prompt, model.Preview); // every word, nothing added or left out
        Assert.Equal(Encoding.UTF8.GetBytes(prompt), model.FileBytes); // the prompt as built: UTF-8, no byte order mark
        Assert.NotEqual(0xEF, model.FileBytes[0]);
        Assert.Equal(prompt.ReplaceLineEndings("\r\n"), model.ClipboardText);
        Assert.DoesNotContain("\r\n", model.Preview);
    }

    [Fact]
    public void WithNoPromptTheModelSaysWhyAndOffersNothingToSave()
    {
        var model = new AgentPromptModel();
        model.Failed(new RegexMatchTimeoutException());
        Assert.False(model.Ready);
        Assert.Equal(AgentPromptText.RedactionGaveUp, model.Preview);
        model.Failed(new InvalidOperationException("The service control manager is not available."));
        Assert.Equal("PoolWatch could not read what runs on this PC, so it made no prompt: The service control manager is not available. " +
                     "Close this window and try again.", model.Preview);
        Assert.Empty(model.FileBytes);
    }

    [Fact]
    public void TheThreeStepsAndWhatIsSharedAreTheApprovedWords()
    {
        Assert.Equal(
        [
            ("Save the prompt", "A file with what runs on this PC and what to ask you."),
            ("Give it to your agent", "Claude Code, Codex or any other: \"Read poolwatch-services.md and follow it.\""),
            ("Import its answer", "Your agent saves a file. PoolWatch shows every suggestion before it changes anything."),
        ], AgentPromptText.Steps);
        Assert.Equal("What your agent will see", AgentPromptText.SharedHeading);
        Assert.Equal("Only what is in this file: the names and publishers of the services, scheduled tasks and startup apps below, what each " +
                     "does, and your Windows version.", AgentPromptText.SharedOnly);
        Assert.Equal("Never in it: your name, user or PC name, folder paths, or anything from your files. Nothing is sent until you give the " +
                     "file to your agent. Read every word on the right.", AgentPromptText.NeverLabel + " " + AgentPromptText.NeverText);
        Assert.Equal("poolwatch-services.md: every word your agent will see", AgentPromptText.PreviewHeading);
    }

    // ---------------------------------------------------------------- the prompt window

    private AgentPromptForm PromptForm(Func<string> build) => new(build, _files, new InlineWork(), percent: 100);

    [Fact]
    public void TheWindowShowsEveryWordOfThePromptAndSavesOnlyWhereTheUserPicks()
    {
        string prompt = Prompt();
        using var form = PromptForm(() => prompt);
        Assert.Equal(AgentPromptText.Reading, form.Preview.Text);
        Assert.False(form.SaveButton.Enabled || form.CopyButton.Enabled);
        Click(form.SaveButton); // nothing to save yet: no dialog
        Assert.Empty(_files.SaveNames);

        form.Start();
        Assert.Equal(prompt, form.Preview.Text.ReplaceLineEndings("\n"));
        Assert.True(form.SaveButton.Enabled && form.CopyButton.Enabled);
        Assert.True(form.Preview.ReadOnly);
        Assert.False(form.Preview.DetectUrls); // nothing in it opens anything

        // Cancelled in the Save dialog: nothing is written.
        Click(form.SaveButton);
        Assert.Equal(["poolwatch-services.md"], _files.SaveNames);
        Assert.Empty(_files.Written);

        _files.SavePath = @"C:\Users\someone\Documents\my-prompt.md";
        Click(form.SaveButton);
        Assert.Equal(["poolwatch-services.md", "poolwatch-services.md"], _files.SaveNames);
        var (path, bytes) = Assert.Single(_files.Written);
        Assert.Equal(@"C:\Users\someone\Documents\my-prompt.md", path);
        Assert.Equal(Encoding.UTF8.GetBytes(prompt), bytes);
        Assert.Equal("Saved my-prompt.md.", form.Status.Text);
    }

    [Fact]
    public void AFileThatCannotBeWrittenSaysWhyAndCopyPutsThePromptOnTheClipboard()
    {
        string prompt = Prompt();
        using var form = PromptForm(() => prompt);
        form.Start();
        _files.SavePath = @"C:\Users\someone\Documents\poolwatch-services.md";
        _files.WriteError = AgentFileIo.FolderRefused;
        Click(form.SaveButton);
        Assert.Equal([AgentFileIo.FolderRefused], _files.Told);
        Assert.Equal("", form.Status.Text);

        Click(form.CopyButton);
        Assert.Equal(prompt.ReplaceLineEndings("\r\n"), _files.Copied);
        Assert.Equal(AgentPromptText.Copied, form.Status.Text);
        _files.CopyError = "Another program is using the clipboard. Try again.";
        Click(form.CopyButton);
        Assert.Equal(_files.CopyError, form.Status.Text);
    }

    [Fact]
    public void WhenThePcCannotBeReadTheWindowSaysWhyAndOffersNothingToSave()
    {
        using var form = PromptForm(() => throw new RegexMatchTimeoutException());
        form.Start();
        Assert.Equal(AgentPromptText.RedactionGaveUp, form.Preview.Text);
        Assert.False(form.SaveButton.Enabled || form.CopyButton.Enabled);
    }

    [Fact]
    public void ThePromptFromThisPcNamesItsItemsAndNeverACommandLineOrTheUser()
    {
        using var form = PromptForm(Prompt);
        _ = form.Handle; // never shown
        form.Start();
        string text = form.Preview.Text;
        Assert.StartsWith("# Help me choose which background services to turn off", text);
        Assert.Contains("| Downloaded Maps Manager (MapsBroker) |", text);
        Assert.Contains("| Example Deck | Example | startup app | 0.2 core, 9k page faults/s |", text);
        Assert.Contains("| Example Power Notice | Example Devices Ltd | task |", text);
        Assert.DoesNotContain("OneDrive", text);   // Microsoft's own startup app is left out
        Assert.DoesNotContain("--tray", text);     // a command line never goes in it
        Assert.DoesNotContain(AgentPc.User, text);
        Assert.Contains("Windows 11 Pro 25H2 (build 26220.9757)", text);
        // Its headings are in the design's blue.
        form.Preview.Select(0, 1);
        Assert.Equal(ListTabStyle.Accent.ToArgb(), form.Preview.SelectionColor.ToArgb());
    }

    // ---------------------------------------------------------------- the list the app builds

    [Fact]
    public void TheListKeepsEachStartupAppAsWindowsNamesItAndLeavesOutMicrosoftsOwn()
    {
        var list = _pc.Items();
        var deck = Assert.Single(list.Items, i => i.Kind == AgentItemKind.Startup && i.Id == "Example Deck").Startup!;
        Assert.Equal(("Example Deck", "HKCU Run", "Example", true), (deck.Name, deck.Location, deck.Publisher, deck.Enabled));
        Assert.Equal(0.2, deck.Cost!.Cores);
        // A Startup-folder shortcut keeps its file name: the switch finds it by that name.
        Assert.Equal("Old Launcher.lnk", Assert.Single(list.Items, i => i.Startup?.Location == "Startup folder (you)").Startup!.Name);
        Assert.DoesNotContain(list.Items, i => i.Startup?.Name == "OneDrive");
        var task = Assert.Single(list.Items, i => i.Kind == AgentItemKind.Task && i.Id == "Example Power Notice").Task!;
        Assert.Equal((@"\ExampleVendor\Example Power Notice", "Example Devices Ltd", true), (task.Path, task.Author, task.Enabled));
        // A task with no author is from the folder its program is installed in, as the tab says it.
        Assert.Equal("Example", Assert.Single(list.Items, i => i.Id == "Example Updater").Task!.Author);
        Assert.Equal(5, list.Items.Count(i => i.Kind == AgentItemKind.Service));
    }

    // ---------------------------------------------------------------- the review's model

    [Fact]
    public void TheReviewSaysWhatWillBeTurnedOffWhatIsRefusedAndWhatIsLeftAsItIs()
    {
        var review = AgentReview.Of(_pc.Plan());
        Assert.Equal(4, review.Changes);
        Assert.True(review.CanApply);
        Assert.Equal("Apply 4 changes", review.ApplyText);
        Assert.Equal("Nothing has changed yet. This is exactly what Apply changes on this PC, and what it leaves as it is.", review.Lead);
        var s = review.Sections;
        Assert.Equal(["Will be turned off (4)", "PoolWatch will not turn these off (3)", "Left as they are (4)"], s.Select(x => x.Heading));
        Assert.Equal(
        [
            new("Downloaded Maps Manager (MapsBroker)", "service · stopped and set to not start", "Your agent: You said you never use offline maps."),
            new("Example Device Service (ExampleVendorSvc)", "service · stopped and set to not start", null),
            new("Example Deck", "startup app in HKCU Run · no longer starts at sign-in", "Your agent: The deck is not plugged in & you said so."),
            new AgentReviewRow("Example Power Notice", @"scheduled task \ExampleVendor\Example Power Notice · disabled, so it no longer runs on its own", null),
        ], s[0].Rows);
        Assert.Equal(["Microsoft Defender Antivirus Service (WinDefend)", "NoSuchService", "OneDrive"], s[1].Rows.Select(r => r.Name));
        Assert.Equal(["service", "service", "startup app"], s[1].Rows.Select(r => r.What));
        Assert.StartsWith("PoolWatch never changes it. It is a protected service", s[1].Rows[0].Note);
        Assert.Equal("It is not a service on this PC now, so PoolWatch left it out.", s[1].Rows[1].Note);
        Assert.Contains("Microsoft's are left alone", s[1].Rows[2].Note);
        Assert.Equal(
        [
            new("Fax", "service · off already", null),
            new("Old Launcher.lnk", "startup app · off already", null),
            new("Other Example Service (OtherVendorSvc)", "service · your agent says to keep it", null),
            new AgentReviewRow("NotHere", "not on this PC now, so there is nothing to keep", null),
        ], s[2].Rows);
    }

    /// <summary>
    /// Windows does not stop a service while one that depends on it runs, so the row of a running service that running
    /// services depend on does not say it is stopped: it says when it is, and that else it stops when Windows restarts. A
    /// dependent that is not running is not named; a review read from a file (Load) knows the services too.
    /// </summary>
    [Fact]
    public void AReviewRowSaysWhenARunningServiceThatDependsOnTheOneItStopsKeepsItRunning()
    {
        static ServiceView Dependent(string name, bool running) => new() { Name = name, DisplayName = name, StartType = ServiceStartType.Automatic, Running = running, DependsOn = ["MapsBroker"] };
        string Maps(AgentReview review) => review.Sections[0].Rows.Single(r => r.Name == "Downloaded Maps Manager (MapsBroker)").What;

        _pc.Backend.Live["AcmeMaps"] = Dependent("AcmeMaps", running: true);
        _pc.Backend.Live["AcmeIdle"] = Dependent("AcmeIdle", running: false);
        Assert.Equal("service · set to not start; stopped if AcmeMaps, which depends on it, has stopped by then, else it stops when Windows restarts",
            Maps(AgentReview.Of(_pc.Plan(), _pc.Items().Options)));
        _files.Answer = Encoding.UTF8.GetBytes(AgentPc.Answer);
        Assert.Equal(Maps(AgentReview.Of(_pc.Plan(), _pc.Items().Options)), Maps(AgentReview.Load("a.json", _files, _pc.Items)));

        _pc.Backend.Live["AcmeSync"] = Dependent("AcmeSync", running: true);
        Assert.Equal("service · set to not start; stopped if AcmeMaps and AcmeSync, which depend on it, have stopped by then, else it stops when " +
                     "Windows restarts", Maps(AgentReview.Of(_pc.Plan(), _pc.Items().Options)));
        Assert.Equal("service · stopped and set to not start",
            AgentReview.Of(_pc.Plan(), _pc.Items().Options).Sections[0].Rows.Single(r => r.Name.EndsWith("(ExampleVendorSvc)", StringComparison.Ordinal)).What);
    }

    [Fact]
    public void ServicesOfOtherAppsAreAskedAboutAgainNamingEach()
    {
        var review = AgentReview.Of(_pc.Plan());
        Assert.Equal(["ExampleVendorSvc"], review.Unknown.Select(u => u.Option.Service.Name));
        Assert.Equal("This service is from other apps, and PoolWatch does not recognise it:\n\n  Example Device Service (ExampleVendorSvc)\n\n" +
                     "Yes turns it off with the rest. No leaves it as it is.", review.UnknownQuestion);
        Assert.Equal(["MapsBroker", "ExampleVendorSvc"], review.ServicesToApply(withUnknown: true));
        Assert.Equal(["MapsBroker"], review.ServicesToApply(withUnknown: false));
    }

    [Fact]
    public void AnAnswerThatChangesNothingHasNothingToApply()
    {
        var review = AgentReview.Of(_pc.Plan("""{ "version": 1, "turnOff": { "services": ["Fax"] }, "keep": { "services": ["MapsBroker"] } }"""));
        Assert.False(review.CanApply);
        Assert.Equal("Apply", review.ApplyText);
        Assert.Equal("Nothing in this answer changes this PC, so there is nothing to apply.", review.Lead);
        Assert.Equal(["Left as they are (2)"], review.Sections.Select(x => x.Heading));
    }

    [Fact]
    public void ABadFileIsRefusedInPlainWordsAndNothingIsPlanned()
    {
        _files.Answer = Encoding.UTF8.GetBytes("""{ "version": 1, "turnOff": { "services": ["MapsBroker"] }, "run": "anything" }""");
        var review = AgentReview.Load(@"C:\answers\poolwatch-services-answer.json", _files, _pc.Items);
        Assert.Null(review.Plan);
        Assert.False(review.CanApply);
        Assert.Empty(review.Sections);
        Assert.Equal("Nothing was changed. The answer has a field PoolWatch does not know: \"run\". It reads only version, turnOff, keep and why.", review.Lead);
        // The file is read bounded: one byte more than an answer may be, so a larger one is refused for its size.
        Assert.Equal((@"C:\answers\poolwatch-services-answer.json", AgentAnswer.MaxBytes + 1), Assert.Single(_files.Reads));

        _files.Answer = Encoding.UTF8.GetBytes("not json");
        Assert.Equal("Nothing was changed. The answer is not valid JSON (line 1).", AgentReview.Load("a.json", _files, _pc.Items).Lead);
        _files.Answer = new byte[AgentAnswer.MaxBytes + 1];
        Assert.Contains("larger than 64 KB", AgentReview.Load("a.json", _files, _pc.Items).Lead);
        _files.ReadError = AgentFileIo.LinkRefused;
        Assert.Equal("Nothing was changed. " + AgentFileIo.LinkRefused, AgentReview.Load("a.json", _files, _pc.Items).Lead);
        _files.ReadError = null;
        _files.Answer = Encoding.UTF8.GetBytes(AgentPc.Answer);
        Assert.Equal(AgentReview.RedactionGaveUp, AgentReview.Load("a.json", _files, () => throw new RegexMatchTimeoutException()).Error);
        Assert.Equal(4, AgentReview.Load("a.json", _files, _pc.Items).Changes);
    }

    // ---------------------------------------------------------------- Apply

    [Fact]
    public void ApplyRecordsEveryChangeFirstSoActiveChangesListsItAsTheAnswers()
    {
        var plan = _pc.Plan();
        var results = _pc.Apply(plan, ["MapsBroker", "ExampleVendorSvc"]);

        Assert.Equal([("MapsBroker", null), ("ExampleVendorSvc", null), ("Example Deck", null), ("Example Power Notice", null)], results);
        Assert.Equal(["apply MapsBroker", "apply ExampleVendorSvc"], _pc.Backend.Calls);
        Assert.Equal(["startup HKCU Run\\Example Deck False", @"task \ExampleVendor\Example Power Notice False"], _pc.Changes.Toggles.Calls);
        // Each change says where it came from, and the services' originals are kept for Put back.
        Assert.All(_pc.Services.Changes, c => Assert.Equal(AgentAnswer.Via, c.Via));
        Assert.Equal([("MapsBroker", ServiceStartType.Automatic, true), ("ExampleVendorSvc", ServiceStartType.Automatic, true)],
            _pc.Services.Changes.Select(c => (c.Service, c.OriginalStart, c.OriginalRunning)));
        var records = _pc.Changes.Records.All();
        Assert.Equal([(RecordKind.StartupEntry, "HKCU Run", "Example Deck", true), (RecordKind.ScheduledTask, @"\ExampleVendor\Example Power Notice", "Example Power Notice", true)],
            records.Select(r => (r.Kind, r.Location!, r.Name!, r.EnabledBefore!.Value)));
        Assert.All(records, r => Assert.Equal(AgentAnswer.Via, r.Via));
        // Active changes lists all four, each with a way to put it back.
        var active = _pc.ActiveChanges();
        Assert.Equal([ChangeKind.Service, ChangeKind.Service, ChangeKind.ScheduledTask, ChangeKind.StartupEntry], active.Select(a => a.Kind).Order());
        Assert.All(active, a => Assert.True(a.CanUndo));
        // The timeline has each switch, as the Tasks and startup apps tab writes it.
        Assert.Equal(["Example Deck", "Example Power Notice"], _pc.Timeline.Select(t => t.Name));
        Assert.StartsWith("Startup app set not to start at sign-in by you: HKCU Run\\Example Deck", _pc.Timeline[0].What);
        Assert.Equal("MapsBroker, ExampleVendorSvc, Example Deck, Example Power Notice: done\n\nActive changes lists each change, with a way to put it back.",
            AgentReview.ResultText(results));
    }

    [Fact]
    public void ApplyTurnsOffOnlyThePlansServicesAndOneFailureStopsNoneOfTheOthers()
    {
        var plan = _pc.Plan();
        _pc.Changes.Toggles.Error = "the entry is gone";
        // A name that is not in the plan is never turned off, even when it is asked for.
        var results = _pc.Apply(plan, ["MapsBroker", "WinDefend", "OtherVendorSvc"]);
        Assert.Equal(["apply MapsBroker"], _pc.Backend.Calls);
        Assert.Equal([("MapsBroker", null), ("Example Deck", "the entry is gone"), ("Example Power Notice", "the entry is gone")], results);
        Assert.Empty(_pc.Changes.Records.All()); // a switch that failed takes its record back
        Assert.Empty(_pc.Timeline);
        Assert.Equal("MapsBroker: done\nExample Deck, Example Power Notice: the entry is gone\n\nActive changes lists each change, with a way to put it back.",
            AgentReview.ResultText(results));
        Assert.Equal("Nothing was changed.", AgentReview.ResultText([]));
    }

    [Fact]
    public void AServiceGoneSinceTheReviewStopsNoneOfTheOthersAndTheResultSaysWhichWasNotTurnedOff()
    {
        var plan = _pc.Plan();
        _pc.Backend.Live.Remove("ExampleVendorSvc"); // uninstalled while the review was open
        var results = _pc.Apply(plan, ["MapsBroker", "ExampleVendorSvc"]);

        Assert.Equal(["apply MapsBroker"], _pc.Backend.Calls);
        Assert.Equal(["MapsBroker"], _pc.Services.Changes.Select(c => c.Service));
        Assert.Equal([("MapsBroker", null), ("ExampleVendorSvc", "'ExampleVendorSvc' is not an installed service on this PC"),
            ("Example Deck", null), ("Example Power Notice", null)], results);
        Assert.Equal("MapsBroker, Example Deck, Example Power Notice: done\nExampleVendorSvc: 'ExampleVendorSvc' is not an installed service on this PC\n\n" +
                     "Active changes lists each change, with a way to put it back.", AgentReview.ResultText(results));

        // A plan of that one service: the gate's refusal is said as that service's.
        var one = _pc.Plan("""{ "version": 1, "turnOff": { "services": ["OtherVendorSvc"] } }""");
        _pc.Backend.Live.Remove("OtherVendorSvc");
        Assert.Equal([("OtherVendorSvc", "'OtherVendorSvc' is not an installed service on this PC")], _pc.Apply(one, ["OtherVendorSvc"]));
    }

    [Fact]
    public void AServiceGoneFromALongPlanIsFoundWithOneMoreReadOfTheServicesAndTheRestAreTurnedOff()
    {
        for (int i = 0; i < 20; i++)
            _pc.Backend.Add($"Vendor{i:00}Svc", $"Vendor Service {i:00}", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Vendor Ltd");
        string names = string.Join(", ", Enumerable.Range(0, 20).Select(i => $"\"Vendor{i:00}Svc\""));
        var plan = _pc.Plan($$"""{ "version": 1, "turnOff": { "services": [{{names}}] } }""");
        _pc.Backend.Live.Remove("Vendor07Svc"); // uninstalled while the review was open
        _pc.Backend.Reads = 0;

        var results = _pc.Apply(plan, plan.Services.Select(s => s.Option.Service.Name).ToList());
        // The whole list (refused), one read to find what the gate refuses on its own, then the rest in one go.
        Assert.Equal(3, _pc.Backend.Reads);
        Assert.Equal(19, _pc.Services.Changes.Count);
        Assert.Equal(19, results.Count(r => r.Error is null));
        Assert.Equal(("Vendor07Svc", "'Vendor07Svc' is not an installed service on this PC"), Assert.Single(results, r => r.Error is not null));
    }

    [Fact]
    public void ServicesThatCannotBeReadAreSaidOnceAndNotTriedAgainForEach()
    {
        var plan = _pc.Plan();
        _pc.Backend.Fail = new InvalidOperationException("The service control manager is not available.");
        _pc.Backend.Reads = 0;
        var results = _pc.Apply(plan, ["MapsBroker", "ExampleVendorSvc"]);

        Assert.Equal(1, _pc.Backend.Reads);
        Assert.Empty(_pc.Backend.Calls);
        Assert.Equal([(AgentApply.ServicesFailed, "The service control manager is not available."), ("Example Deck", null), ("Example Power Notice", null)], results);
        Assert.Equal("The service control manager is not available.\nExample Deck, Example Power Notice: done\n\n" +
                     "Active changes lists each change, with a way to put it back.", AgentReview.ResultText(results));
    }

    [Fact]
    public void AServiceTheGateRefusesOnlyOnAnInsiderBuildIsSetAsideAndTheRestTurnedOff()
    {
        // A third party's "wisvc" may be turned off; once the PC is enrolled in Windows Insider, the gate protects the name.
        // The controller finds it with the gate's own reads of the made-up PC (its services, which carry the flag in their
        // group, through the backend, as Apply reads them), so this host's own flag plays no part. That RefusedAlone also
        // passes the backend's flag itself, as the gate does, is defence in depth this test does not single out: the group
        // alone refuses wisvc here.
        _pc.Backend.Add("wisvc", "Example Insider Helper", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Ltd");
        var plan = _pc.Plan("""{ "version": 1, "turnOff": { "services": ["MapsBroker", "wisvc"] } }""");
        Assert.Equal(["MapsBroker", "wisvc"], plan.Services.Select(s => s.Option.Service.Name).Order(StringComparer.Ordinal));
        _pc.Backend.Insider = true; // enrolled since the review was read: the gate refuses the whole list
        var results = _pc.Apply(plan, ["MapsBroker", "wisvc"]);

        // wisvc is set aside with the gate's reason, and MapsBroker is turned off on its own.
        Assert.Equal(["apply MapsBroker"], _pc.Backend.Calls);
        Assert.Contains(("MapsBroker", (string?)null), results);
        Assert.Contains(("wisvc", (string?)"'wisvc' is a protected service and is never changed"), results);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ServicesThatCannotBeReadWhileTheRefusedOneIsSoughtStopNoneOfTheOtherChanges()
    {
        var plan = _pc.Plan();
        _pc.Backend.Live.Remove("ExampleVendorSvc"); // the gate refuses the whole list
        _pc.Backend.Fail = new InvalidOperationException("The service control manager is not available.");
        _pc.Backend.Reads = 0;
        _pc.Backend.FailOnRead = 2; // the read that looks for the one refused
        var results = _pc.Apply(plan, ["MapsBroker", "ExampleVendorSvc"]);

        // The gate decides the services again (still refused, nothing changed), and the startup app and task are switched.
        Assert.Equal(3, _pc.Backend.Reads);
        Assert.Empty(_pc.Backend.Calls);
        Assert.Equal([(AgentApply.PlanRefused, "'ExampleVendorSvc' is not an installed service on this PC"), ("Example Deck", null), ("Example Power Notice", null)], results);
    }

    [Fact]
    public void AFailureOfTheOneServiceLeftAfterTheRefusedOneIsSaidAsThatServices()
    {
        var plan = _pc.Plan();
        _pc.Backend.Live.Remove("ExampleVendorSvc"); // the gate refuses the whole list
        _pc.Backend.Fail = new InvalidOperationException("The service control manager is not available.");
        _pc.Backend.Reads = 0;
        _pc.Backend.FailOnRead = 3; // the Apply of the one service left
        var results = _pc.Apply(plan, ["MapsBroker", "ExampleVendorSvc"]);

        // Nothing is changed for either service, and the message says which service each reason is about.
        Assert.Equal(3, _pc.Backend.Reads);
        Assert.Empty(_pc.Backend.Calls);
        Assert.Equal([("MapsBroker", "The service control manager is not available."), ("ExampleVendorSvc", "'ExampleVendorSvc' is not an installed service on this PC"),
            ("Example Deck", null), ("Example Power Notice", null)], results);
        Assert.Equal("MapsBroker: The service control manager is not available.\nExampleVendorSvc: 'ExampleVendorSvc' is not an installed service on this PC\n" +
                     "Example Deck, Example Power Notice: done\n\nActive changes lists each change, with a way to put it back.", AgentReview.ResultText(results));
    }

    [Fact]
    public void TheMessageAfterApplyGivesAtMostTenReasonsAndCountsTheItemsOfTheRest()
    {
        var results = new List<(string Name, string? Error)> { ("MapsBroker", null) };
        results.AddRange(Enumerable.Range(0, 14).Select(i => ($"Vendor{i:00}Svc", (string?)$"'Vendor{i:00}Svc' is not an installed service on this PC")));
        results.Add(("Example Deck", null));
        var lines = AgentReview.ResultText(results).Split('\n');
        Assert.Equal("MapsBroker, Example Deck: done", lines[0]);
        Assert.Equal(Enumerable.Range(0, AgentReview.MaxReasons).Select(i => $"Vendor{i:00}Svc: 'Vendor{i:00}Svc' is not an installed service on this PC"), lines[1..11]);
        Assert.Equal(["and 4 more not changed, for other reasons", "", "Active changes lists each change, with a way to put it back."], lines[11..]);
    }

    /// <summary>
    /// A service whose start type was set but that is not known to have stopped is a change: past the tenth reason it is
    /// counted apart, never as not changed, and the message says where to put it back, even when nothing else changed.
    /// </summary>
    [Fact]
    public void TheMessageAfterApplyNeverCountsAServiceWhoseStartTypeWasSetAsNotChanged()
    {
        static string NotStopped(string dependent) => ServicePlan.NotStoppedText(ServiceStartType.Disabled, ServiceControl.Answered(1051, lookedAgain: false).Why!, [dependent]);
        var results = Enumerable.Range(0, AgentReview.MaxReasons).Select(i => ($"Vendor{i:00}Svc", (string?)$"'Vendor{i:00}Svc' is not an installed service on this PC")).ToList();
        results.Add(("MapsBroker", NotStopped("AcmeMaps"))); // the eleventh reason
        results.Add(("SysMain", NotStopped("AcmeSync")));
        results.Add(("Vendor99Svc", "'Vendor99Svc' is not an installed service on this PC"));
        var lines = AgentReview.ResultText(results).Split('\n');
        Assert.Equal(Enumerable.Range(0, AgentReview.MaxReasons).Select(i => $"Vendor{i:00}Svc: 'Vendor{i:00}Svc' is not an installed service on this PC"), lines[..10]);
        Assert.Equal(["and 2 more set to Disabled but not known to have stopped, for other reasons", "and 1 more not changed, for other reasons", "",
                      "Active changes lists each change, with a way to put it back."], lines[10..]);

        // Within the ten, such a result is a line of its own, with its reason.
        Assert.Equal("MapsBroker: " + NotStopped("AcmeMaps") + "\n\nActive changes lists each change, with a way to put it back.",
            AgentReview.ResultText([("MapsBroker", NotStopped("AcmeMaps"))]));
    }

    [Fact]
    public void EveryItemPoolWatchRefusesIsListedWithWhyAndWhatIsLeftAsItIsIsCounted()
    {
        string gone = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"\"Gone{i:00}\""));
        string away = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"\"Away{i:00}\""));
        var review = AgentReview.Of(_pc.Plan($$"""{ "version": 1, "turnOff": { "services": [{{gone}}] }, "keep": { "services": [{{away}}] } }"""));

        var refused = review.Sections[0];
        Assert.Equal("PoolWatch will not turn these off (60)", refused.Heading);
        Assert.Equal(Enumerable.Range(0, 60).Select(i => $"Gone{i:00}"), refused.Rows.Select(r => r.Name));
        Assert.All(refused.Rows, r => Assert.Equal("It is not a service on this PC now, so PoolWatch left it out.", r.Note));
        Assert.Null(refused.MoreText);

        var left = review.Sections[1];
        Assert.Equal("Left as they are (60)", left.Heading);
        Assert.Equal(AgentReview.MaxListed, left.Rows.Count);
        Assert.Equal("and 10 more, not listed here", left.MoreText);
    }

    [Fact]
    public void AMicrosoftStartupAppIsRefusedByTheSwitchEvenWhenAPlanHoldsOne()
    {
        // The list never offers one (the Core leaves Microsoft's out), so this plan is made by hand: the switch refuses it again.
        var oneDrive = new AgentStartupEntry("OneDrive", "HKCU Run", "Microsoft Corporation", Enabled: true, Microsoft: true);
        var plan = new AgentPlan { Startup = [new PlannedStartup(new AgentPlanItem(AgentItemKind.Startup, "OneDrive", "OneDrive", null), oneDrive)] };
        Assert.Equal([("OneDrive", ServicesPageText.MicrosoftLeftAlone)], _pc.Apply(plan, []));
        Assert.Empty(_pc.Changes.Toggles.Calls);
        Assert.Empty(_pc.Changes.Records.All());
        Assert.Empty(_pc.Timeline);
    }

    [Fact]
    public void InTheCiUiTestApplyChangesNothingAndSaysSo()
    {
        bool ran = false;
        List<(string Name, string? Error)> Run() { ran = true; return [("MapsBroker", null)]; }
        var refused = AgentApply.UnlessTestRun(smoke: true, Run);
        Assert.False(ran);
        Assert.Equal([("(test run)", RecommendationActions.Refused)], refused);
        Assert.Equal(RecommendationActions.Refused, AgentReview.ResultText(refused));
        Assert.Equal([("MapsBroker", null)], AgentApply.UnlessTestRun(smoke: false, Run));
        Assert.True(ran);
    }

    [Fact]
    public void CheckingTheAnswerTakingTooLongIsSaidToBeTheAnswersNotThisPcs()
    {
        _files.Answer = Encoding.UTF8.GetBytes(AgentPc.Answer);
        var review = AgentReview.Load("a.json", _files, _pc.Items, (_, _) => throw new RegexMatchTimeoutException());
        Assert.Null(review.Plan);
        Assert.Equal(AgentReview.AnswerTookTooLong, review.Error);
        Assert.DoesNotContain("what runs on this PC", review.Lead);
        // The list's own redaction giving up is still said to be this PC's.
        Assert.Equal(AgentReview.RedactionGaveUp, AgentReview.Load("a.json", _files, () => throw new RegexMatchTimeoutException(), (_, _) => throw new InvalidOperationException()).Error);
    }

    [Fact]
    public void TheQuestionAndTheMessageAfterApplyNameAtMostFifteenAndCountTheRest()
    {
        for (int i = 0; i < 20; i++)
            _pc.Backend.Add($"Vendor{i:00}Svc", $"Vendor Service {i:00}", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Vendor Ltd");
        string names = string.Join(", ", Enumerable.Range(0, 20).Select(i => $"\"Vendor{i:00}Svc\""));
        var review = AgentReview.Of(_pc.Plan($$"""{ "version": 1, "turnOff": { "services": [{{names}}] } }"""));
        Assert.Equal(20, review.Unknown.Count);

        var lines = review.UnknownQuestion.Split('\n');
        Assert.Equal("These services are from other apps, and PoolWatch does not recognise them:", lines[0]);
        Assert.Equal(Enumerable.Range(0, 15).Select(i => $"  Vendor Service {i:00} (Vendor{i:00}Svc)").Append("  and 5 more (listed under Will be turned off)"),
            lines.Skip(2).Take(16));
        Assert.Equal(["", "Yes turns them off with the rest. No leaves them as they are."], lines.Skip(18));

        var results = Enumerable.Range(0, 20).Select(i => ($"Vendor{i:00}Svc", (string?)null)).Append(("Example Deck", "the entry is gone")).ToList();
        Assert.Equal(string.Join(", ", Enumerable.Range(0, 15).Select(i => $"Vendor{i:00}Svc")) + ", and 5 more: done\nExample Deck: the entry is gone\n\n" +
                     "Active changes lists each change, with a way to put it back.", AgentReview.ResultText(results));
    }

    // ---------------------------------------------------------------- the review window

    private AgentReviewForm ReviewForm(List<IReadOnlyCollection<string>> applied) =>
        new(() => AgentReview.Of(_pc.Plan()), (plan, services) => { applied.Add(services); return _pc.Apply(plan, services); }, _files, new InlineWork(), percent: 100);

    [Fact]
    public void TheWindowListsEachPartAndCancelChangesNothing()
    {
        var applied = new List<IReadOnlyCollection<string>>();
        var form = ReviewForm(applied);
        try
        {
            Assert.Equal(AgentReview.Reading, form.Status.Text);
            Assert.False(form.ApplyButton.Enabled);
            form.Start();
            Assert.Equal("Apply 4 changes", form.ApplyButton.Text);
            Assert.True(form.ApplyButton.Enabled);
            Assert.Equal("Cancel", form.CancelChanges.Text);
            var words = Words(form);
            Assert.Equal("WILL BE TURNED OFF (4)", words[0]);
            Assert.Contains("Downloaded Maps Manager (MapsBroker)", words);
            Assert.Contains("PoolWatch never changes it. It is a protected service: Windows security.", words);
            Assert.Contains("LEFT AS THEY ARE (4)", words);
            // The list is one control that draws its lines; a screen reader reads each line whole.
            Assert.Empty(form.List.Controls);
            var spoken = Enumerable.Range(0, form.List.AccessibilityObject.GetChildCount()).Select(i => form.List.AccessibilityObject.GetChild(i)!).ToList();
            Assert.Equal(form.List.Entries.Count, spoken.Count);
            var deck = Assert.Single(spoken, a => a.Name!.StartsWith("Example Deck.", StringComparison.Ordinal));
            Assert.Equal("Example Deck. startup app in HKCU Run · no longer starts at sign-in. Your agent: The deck is not plugged in & you said so.", deck.Name);
            Assert.Equal(AccessibleRole.ListItem, deck.Role);
            Assert.Equal(("WILL BE TURNED OFF (4)", AccessibleRole.StaticText), (spoken[0].Name, spoken[0].Role));
            // An answer's words are shown as they are: an ampersand is not a shortcut key.
            Assert.True(AgentReviewList.Flags.HasFlag(TextFormatFlags.NoPrefix));

            Click(form.CancelChanges);
            Assert.Empty(applied);
            Assert.Empty(_pc.Backend.Calls);
            Assert.Empty(_pc.Changes.Toggles.Calls);
            Assert.Empty(_pc.Changes.Records.All());
            Assert.Empty(_pc.Services.Changes);
            Assert.Empty(_files.Told);
        }
        finally { form.Dispose(); }
    }

    /// <summary>Every word the review's list shows, in order: each heading or line, and each row's name, what it is and its line more.</summary>
    internal static List<string> Words(AgentReviewForm form) =>
        form.List.Entries.SelectMany(e => e.Row is { } r ? new[] { r.Name, r.What, r.Note } : [e.Text]).OfType<string>().ToList();

    [Fact]
    public void ApplyAsksAboutServicesOfOtherAppsThenMakesTheChangesAndSaysWhatItDid()
    {
        var applied = new List<IReadOnlyCollection<string>>();
        var form = ReviewForm(applied);
        try
        {
            form.Start();
            _files.Yes = false; // leave the service PoolWatch does not recognise as it is
            Click(form.ApplyButton);
            Assert.Single(_files.Questions);
            Assert.Contains("Example Device Service (ExampleVendorSvc)", _files.Questions[0]);
            Assert.Equal(["MapsBroker"], Assert.Single(applied));
            Assert.Equal(["apply MapsBroker"], _pc.Backend.Calls);
            Assert.Equal(2, _pc.Changes.Records.All().Count);
            Assert.Equal(3, _pc.ActiveChanges().Count);
            Assert.Equal("MapsBroker, Example Deck, Example Power Notice: done\n\nActive changes lists each change, with a way to put it back.",
                Assert.Single(_files.Told));
            Assert.Equal(_files.Told[0], form.Result);
            Assert.False(form.Busy);
        }
        finally { form.Dispose(); }
    }

    [Fact]
    public void YesTurnsOffTheServicesOfOtherAppsToo()
    {
        var applied = new List<IReadOnlyCollection<string>>();
        var form = ReviewForm(applied);
        try
        {
            form.Start();
            _files.Yes = true;
            Click(form.ApplyButton);
            Assert.Equal(["MapsBroker", "ExampleVendorSvc"], Assert.Single(applied));
            Assert.Equal(["apply MapsBroker", "apply ExampleVendorSvc"], _pc.Backend.Calls);
        }
        finally { form.Dispose(); }
    }

    [Fact]
    public void AnAnswerThatWasNotReadOffersOnlyClose()
    {
        var applied = new List<IReadOnlyCollection<string>>();
        var form = new AgentReviewForm(() => AgentReview.Failed("The answer is not valid JSON (line 1)."), (_, s) => { applied.Add(s); return []; }, _files, new InlineWork(), 100);
        try
        {
            form.Start();
            Assert.Equal("Nothing was changed. The answer is not valid JSON (line 1).", form.Lead.Text);
            Assert.False(form.ApplyButton.Visible && form.ApplyButton.Enabled);
            Assert.Equal("Close", form.CancelChanges.Text);
            Assert.Empty(form.List.Entries);
            Click(form.ApplyButton);
            Assert.Empty(applied);
        }
        finally { form.Dispose(); }
    }

    [Fact]
    public void AReadThatFailsSaysSoAndOffersNothingToApply()
    {
        var form = new AgentReviewForm(() => throw new InvalidOperationException("Task Scheduler is not available"), (_, _) => [], _files, new InlineWork(), 100);
        try
        {
            form.Start();
            Assert.Equal("Nothing was changed. PoolWatch could not read what runs on this PC: Task Scheduler is not available.", form.Lead.Text);
            Assert.False(form.ApplyButton.Enabled);
        }
        finally { form.Dispose(); }
    }

    /// <summary>Every control in <paramref name="c"/>, itself included.</summary>
    private static int ControlsIn(Control c) => 1 + c.Controls.Cast<Control>().Sum(ControlsIn);

    [Fact]
    public void TheLargestReviewTheCoreAcceptsIsOneListOfEveryChangeShownQuickly()
    {
        // A PC with as many services of other apps, startup apps and scheduled tasks as one answer may name, and an answer
        // that turns off every one of them and keeps as many names that are not on this PC: as large as the Core reads.
        int max = AgentAnswer.MaxNamesPerList;
        for (int i = 0; i < max; i++)
        {
            _pc.Backend.Add($"Bulk{i:000}", $"Bulk Service {i:000}", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Bulk Ltd");
            _pc.Startup.Add(new($"Bulk App {i:000}", @"C:\Program Files\Bulk\app.exe", "HKCU Run", Enabled: true, Microsoft: false));
            _pc.Tasks.Add(new($@"\Bulk\Bulk Task {i:000}", $"Bulk Task {i:000}", "Example Bulk Ltd", Enabled: true, @"C:\Program Files\Bulk\task.exe", ""));
        }
        string List(string format) => string.Join(",", Enumerable.Range(0, max).Select(i => "\"" + string.Format(CultureInfo.InvariantCulture, format, i) + "\""));
        string json = $$"""
            { "version": 1,
              "turnOff": { "services": [{{List("Bulk{0:000}")}}], "startup": [{{List("Bulk App {0:000}")}}], "tasks": [{{List("Bulk Task {0:000}")}}] },
              "keep": { "services": [{{List("Gone{0:000}")}}], "startup": [{{List("Gone App {0:000}")}}], "tasks": [{{List("Gone Task {0:000}")}}] } }
            """;
        Assert.True(Encoding.UTF8.GetByteCount(json) <= AgentAnswer.MaxBytes);
        var review = AgentReview.Of(_pc.Plan(json));
        Assert.Equal(3 * max, review.Changes);

        using var small = new AgentReviewForm(() => AgentReview.Of(_pc.Plan()), (_, _) => [], _files, new InlineWork(), 100);
        using var form = new AgentReviewForm(() => review, (_, _) => [], _files, new InlineWork(), 100);
        _ = small.Handle; // never shown
        _ = form.Handle;
        Assert.True(form.List.Width > 300, $"the list is {form.List.Size}"); // laid out at the window's size
        small.Start();
        var time = Stopwatch.StartNew();
        form.Start(); // shows the review and lays out its list, on this (the window's) thread
        time.Stop();

        // Every change is listed, each by its name; what changes nothing is listed up to its bound and counted.
        var entries = form.List.Entries;
        Assert.Equal($"WILL BE TURNED OFF ({3 * max})", entries[0].Text);
        Assert.Equal(3 * max, entries.Count(e => e.Row is { What: var w } && !w.Contains("nothing to keep", StringComparison.Ordinal)));
        Assert.Equal($"LEFT AS THEY ARE ({3 * max})", entries[3 * max + 1].Text);
        Assert.Equal(AgentReview.MaxListed, entries.Skip(3 * max + 2).Count(e => e.Row is not null));
        Assert.Equal($"and {3 * max - AgentReview.MaxListed} more, not listed here", entries[^1].Text);
        // The "and N more" line: one piece in the list's own font and muted, never drawn as a heading.
        Assert.Equal(AgentReviewList.EntryKind.More, entries[^1].Kind);
        var more = Assert.Single(form.List.Parts(entries.Count - 1));
        Assert.Equal(AgentReviewList.Tone.Muted, more.Tone);
        Assert.Same(form.List.Font, more.Font);
        Assert.Equal($"Apply {3 * max} changes", form.ApplyButton.Text);
        // One list whatever the answer holds: no more controls than the smallest review, and no window per line.
        Assert.Empty(form.List.Controls);
        Assert.Equal(ControlsIn(small), ControlsIn(form));
        Assert.True(form.List.EntryBounds(entries.Count - 1).Bottom > form.List.ClientSize.Height); // the rest is scrolled to
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(5), $"showing the largest review took {time.Elapsed.TotalSeconds:0.00} s");
    }

    /// <summary>The pixels in <paramref name="r"/> of <paramref name="bitmap"/>, row by row, as ARGB values.</summary>
    private static int[] PixelsOf(Bitmap bitmap, Rectangle r)
    {
        var data = bitmap.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var all = new int[r.Width * r.Height];
            for (int y = 0; y < r.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, all, y * r.Width, r.Width);
            return all;
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>The pixels in <paramref name="r"/> of <paramref name="bitmap"/> that are not <paramref name="paper"/>.</summary>
    private static int Ink(Bitmap bitmap, Rectangle r, Color paper)
    {
        int background = paper.ToArgb();
        return PixelsOf(bitmap, r).Count(p => p != background);
    }

    /// <summary>The darkest channel of any pixel in <paramref name="r"/> of <paramref name="bitmap"/>.</summary>
    private static int Darkest(Bitmap bitmap, Rectangle r) =>
        PixelsOf(bitmap, r).Select(Color.FromArgb).Min(p => Math.Min(p.R, Math.Min(p.G, p.B)));

    /// <summary>
    /// Each line of the review is its pieces of text, placed once: an item's name in bold, what it is, and its line more
    /// when it has one, in that order, each inside the item and above the next piece, and the item above the next line;
    /// and each piece is drawn where it was placed.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public void TheReviewListDrawsEachItemsNameWhatItIsAndItsNoteEachInItsOwnPlace(int percent)
    {
        using var form = new AgentReviewForm(() => AgentReview.Of(_pc.Plan()), (_, _) => [], _files, new InlineWork(), percent);
        using var host = Hosted(form); // never shown
        form.Start();
        Assert.True(form.List.Created);
        var list = form.List;
        var entries = list.Entries;
        Assert.Equal(14, entries.Count); // three headings and eleven items
        var nameFont = list.Parts(Enumerable.Range(0, entries.Count).First(k => entries[k].Row is not null))[0].Font;
        int withNote = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var parts = list.Parts(i);
            var line = list.EntryBounds(i);
            if (entries[i].Row is { } row)
            {
                Assert.Equal(new[] { row.Name, row.What, row.Note }.OfType<string>(), parts.Select(p => p.Text));
                Assert.Equal(new[] { AgentReviewList.Tone.Ink, AgentReviewList.Tone.Faint, AgentReviewList.Tone.Muted }.Take(parts.Count), parts.Select(p => p.Tone));
                Assert.NotSame(list.Font, parts[0].Font); // the name in the bold font, the rest in the list's
                Assert.All(parts.Skip(1), p => Assert.Same(list.Font, p.Font));
                if (parts.Count == 3)
                {
                    Assert.Equal(2 * percent / 100, parts[2].Bounds.Top - parts[1].Bounds.Bottom); // a small gap above the line more
                    withNote++;
                }
            }
            else
            {
                // A heading: in the small font (neither the list's nor an item name's), faint.
                Assert.Equal(AgentReviewList.EntryKind.Heading, entries[i].Kind);
                Assert.Equal([entries[i].Text], parts.Select(p => p.Text));
                Assert.Equal(AgentReviewList.Tone.Faint, parts[0].Tone);
                Assert.NotSame(list.Font, parts[0].Font);
                Assert.NotSame(nameFont, parts[0].Font);
            }
            foreach (var p in parts)
            {
                Assert.True(p.Bounds.Height > 0 && line.Contains(p.Bounds), $"\"{p.Text}\" is at {p.Bounds}, outside its line at {line}");
                Assert.True(TextRenderer.MeasureText(p.Text, p.Font, new Size(p.Bounds.Width, int.MaxValue), AgentReviewList.Flags).Height <= p.Bounds.Height,
                    $"\"{p.Text}\" needs more than its {p.Bounds.Height} px");
            }
            for (int k = 0; k + 1 < parts.Count; k++)
                Assert.True(parts[k].Bounds.Bottom <= parts[k + 1].Bounds.Top, $"\"{parts[k].Text}\" runs into \"{parts[k + 1].Text}\"");
            if (i + 1 < entries.Count) Assert.True(line.Bottom <= list.EntryBounds(i + 1).Top, $"line {i} runs into line {i + 1}");
        }
        Assert.True(withNote > 0, "no item has a line more");

        // Each piece in view is drawn in its place, in its tone's colour, and each item in view has a line under it: from
        // the top, then from the end.
        int notes = 0, ruled = 0;
        bool contrast = SystemInformation.HighContrast;
        void Drawn()
        {
            using var bitmap = new Bitmap(list.Width, list.Height);
            list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, list.Size));
            for (int i = 0; i < entries.Count; i++)
            {
                foreach (var p in list.Parts(i).Where(p => list.ClientRectangle.Contains(p.Bounds)))
                {
                    Assert.True(Ink(bitmap, p.Bounds, list.BackColor) > 0, $"\"{p.Text}\" is not drawn at {p.Bounds}");
                    var colour = AgentReviewList.ColourOf(p.Tone, contrast);
                    var off = Unblended(bitmap, p.Bounds, colour, list.BackColor);
                    Assert.True(off is null, $"\"{p.Text}\" ({p.Tone}) has a pixel {off} not between {colour} and the paper");
                    // ...and reaches its tone's colour, so a piece drawn in a lighter tone is caught too (the nearest two
                    // tones are 13 levels apart, so 6 tells them apart with room for the runner's font smoothing).
                    int darkest = Darkest(bitmap, p.Bounds), target = Math.Min(colour.R, Math.Min(colour.G, colour.B));
                    Assert.True(darkest <= target + 6, $"\"{p.Text}\" ({p.Tone}) is never darker than {darkest}: lighter than {colour}");
                    if (p.Tone == AgentReviewList.Tone.Muted && entries[i].Row is not null) notes++;
                }
                var under = list.EntryBounds(i);
                under = Rectangle.Intersect(new Rectangle(under.Left, under.Bottom - 1, under.Width, 1), list.ClientRectangle);
                if (entries[i].Row is not null && under.Height == 1)
                {
                    Assert.True(Ink(bitmap, under, list.BackColor) > 0, $"item {i} has no line under it");
                    ruled++;
                }
            }
        }
        Drawn();
        Press(list, Keys.End);
        Drawn();
        Assert.True(notes > 0, "no item's line more was in view");
        Assert.True(ruled > 0, "no item's line under it was in view");
    }

    /// <summary>
    /// The first pixel in <paramref name="r"/> of <paramref name="bitmap"/> with a channel outside the range from
    /// <paramref name="ink"/> to <paramref name="paper"/> (text drawn in <paramref name="ink"/> on <paramref name="paper"/>
    /// blends the two, and nothing else), or null when there is none.
    /// </summary>
    private static Color? Unblended(Bitmap bitmap, Rectangle r, Color ink, Color paper)
    {
        static bool Within(int c, int a, int b) => c >= Math.Min(a, b) - 2 && c <= Math.Max(a, b) + 2;
        foreach (int argb in PixelsOf(bitmap, r))
        {
            var p = Color.FromArgb(argb);
            if (!Within(p.R, ink.R, paper.R) || !Within(p.G, ink.G, paper.G) || !Within(p.B, ink.B, paper.B)) return p;
        }
        return null;
    }

    [Fact]
    public void EachToneOfTheReviewListIsDrawnInItsOwnColourOrTheSystemsWithHighContrastOn()
    {
        Assert.Equal(ListTabStyle.Ink, AgentReviewList.ColourOf(AgentReviewList.Tone.Ink, highContrast: false));
        Assert.Equal(ListTabStyle.Faint, AgentReviewList.ColourOf(AgentReviewList.Tone.Faint, highContrast: false));
        Assert.Equal(ListTabStyle.Muted, AgentReviewList.ColourOf(AgentReviewList.Tone.Muted, highContrast: false));
        Assert.Equal(SystemColors.WindowText, AgentReviewList.ColourOf(AgentReviewList.Tone.Ink, highContrast: true));
        Assert.Equal(SystemColors.GrayText, AgentReviewList.ColourOf(AgentReviewList.Tone.Faint, highContrast: true));
        Assert.Equal(SystemColors.GrayText, AgentReviewList.ColourOf(AgentReviewList.Tone.Muted, highContrast: true));
    }

    /// <summary>
    /// The window's content moved into a control of its own, at the window's client size, and made with its handles as
    /// showing makes them, under WinForms' hidden parking window, so nothing is shown (a window never shown is never made,
    /// and a list that is not made does not scroll). Dispose it before the window.
    /// </summary>
    private static Control Hosted(AgentWindow window)
    {
        var host = new Panel { Size = window.ClientSize, Font = window.Font };
        window.Controls.Remove(window.Content);
        host.Controls.Add(window.Content);
        host.CreateControl();
        return host;
    }

    private static readonly Keys[] ScrollKeys = [Keys.Up, Keys.Down, Keys.PageUp, Keys.PageDown, Keys.Home, Keys.End];

    /// <summary>As a key pressed on the control does (it has no focus while it is not shown).</summary>
    private static void Press(Control c, Keys key) => On(c, "OnKeyDown", new KeyEventArgs(key));

    /// <summary>Raises one of the control's own events (<paramref name="name"/>: its On method), as Windows would.</summary>
    private static void On(Control c, string name, EventArgs e) =>
        c.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [e.GetType()])!.Invoke(c, [e]);

    private static bool IsInputKey(Control c, Keys key) =>
        (bool)typeof(Control).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, [key])!;

    /// <summary>
    /// The review list is reached with Tab and scrolled with the keys (a line, a page, the top, the end); a review shown
    /// again starts at its top; a new font places its lines again; and a screen reader is told which lines are out of view.
    /// </summary>
    [Fact]
    public void TheReviewListIsScrolledWithTheKeysAndSaysWhichLinesAreOutOfView()
    {
        for (int i = 0; i < 30; i++)
            _pc.Backend.Add($"Vendor{i:00}Svc", $"Vendor Service {i:00}", ServiceStartType.Automatic, running: true, microsoft: false, publisher: "Example Vendor Ltd");
        string names = string.Join(", ", Enumerable.Range(0, 30).Select(i => $"\"Vendor{i:00}Svc\""));
        var review = AgentReview.Of(_pc.Plan($$"""{ "version": 1, "turnOff": { "services": [{{names}}] } }"""));
        using var form = new AgentReviewForm(() => review, (_, _) => [], _files, new InlineWork(), 100);
        using var host = Hosted(form); // never shown
        form.Start();
        var list = form.List;
        int Top() => -list.AutoScrollPosition.Y;
        int line = list.Font.Height, page = list.ClientSize.Height - line, end = list.EntryBounds(list.Entries.Count - 1).Bottom - list.ClientSize.Height;
        Assert.True(end > 2 * page, $"the list is only {end} px longer than its view");

        Assert.True(list.TabStop);
        Assert.Null(form.AcceptButton); // Enter in the list applies nothing
        Assert.All(ScrollKeys, k => Assert.True(IsInputKey(list, k), $"{k} is not the list's"));
        Assert.False(IsInputKey(list, Keys.Tab)); // Tab still moves on to the buttons
        int drawn = 0;
        list.Invalidated += (_, e) => { if (e.InvalidRect.Size == list.ClientSize) drawn++; };
        Press(list, Keys.Down);
        Assert.Equal(line, Top());
        Assert.Equal(1, drawn); // drawn again whole, its focus rectangle with it
        Press(list, Keys.PageDown);
        Assert.Equal(line + page, Top());
        Press(list, Keys.Up);
        Assert.Equal(page, Top());
        Press(list, Keys.PageUp);
        Assert.Equal(0, Top());
        Press(list, Keys.Up); // already at the top
        Assert.Equal(0, Top());
        Press(list, Keys.End);
        Assert.Equal(end, Top());
        Assert.Equal(list.ClientSize.Height, list.EntryBounds(list.Entries.Count - 1).Bottom); // the last line in view
        Press(list, Keys.Down); // already at the end
        Assert.Equal(end, Top());
        Press(list, Keys.Shift | Keys.Up); // not a key of the list's
        Assert.Equal(end, Top());

        // Scrolled with the scroll bar or the wheel, or the focus coming or going: drawn again whole.
        drawn = 0;
        On(list, "OnScroll", new ScrollEventArgs(ScrollEventType.SmallDecrement, 0));
        On(list, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 0, 0, 0));
        On(list, "OnGotFocus", EventArgs.Empty);
        On(list, "OnLostFocus", EventArgs.Empty);
        Assert.Equal(4, drawn);

        // Out of view now: the first line; in view: the last.
        var spoken = list.AccessibilityObject;
        Assert.True(spoken.GetChild(0)!.State.HasFlag(AccessibleStates.Offscreen));
        Assert.False(spoken.GetChild(list.Entries.Count - 1)!.State.HasFlag(AccessibleStates.Offscreen));
        Assert.True(spoken.GetChild(0)!.State.HasFlag(AccessibleStates.ReadOnly));
        Press(list, Keys.Home);
        Assert.Equal(0, Top());
        Assert.False(spoken.GetChild(0)!.State.HasFlag(AccessibleStates.Offscreen));
        Assert.True(spoken.GetChild(list.Entries.Count - 1)!.State.HasFlag(AccessibleStates.Offscreen));

        // A review shown again starts at its top.
        Press(list, Keys.End);
        form.Display(review);
        Assert.Equal(0, Top());

        // A new font places every line again (its pieces in that font), at the same width.
        int before = list.EntryBounds(1).Height;
        using var larger = new Font(list.Font.FontFamily, list.Font.Size * 2);
        list.Font = larger;
        try
        {
            Assert.True(list.EntryBounds(1).Height > before, $"the first item is still {before} px tall");
            Assert.All(list.Parts(1).Skip(1), p => Assert.Same(larger, p.Font));
        }
        finally { list.Font = null; }
    }

    [Fact]
    public void AWordLongerThanTheListIsBrokenOntoLinesNotCutOff()
    {
        using var font = new Font("Segoe UI", 9f);
        var one = TextRenderer.MeasureText("W", font, new Size(300, int.MaxValue), AgentReviewList.Flags);
        var size = TextRenderer.MeasureText(new string('W', 160), font, new Size(300, int.MaxValue), AgentReviewList.Flags);
        Assert.True(size.Width <= 300, $"{size.Width} px wide");
        Assert.True(size.Height >= 3 * one.Height, $"{size.Height} px tall");
    }

    [Fact]
    public void EachWindowDisposesItsFontWithIt()
    {
        var prompt = PromptForm(() => "");
        var review = new AgentReviewForm(() => AgentReview.Failed("x"), (_, _) => [], _files, new InlineWork(), 100);
        var fonts = new[] { prompt.Font, review.Font };
        Assert.All(fonts, f => Assert.True(f.GetHeight() > 0));
        prompt.Dispose();
        review.Dispose();
        Assert.All(fonts, f => Assert.Throws<ArgumentException>(() => f.GetHeight()));
    }

    // ---------------------------------------------------------------- the background work

    /// <summary>Takes this thread's messages, as the app's message loop does, until <paramref name="done"/>.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var limit = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(30), "nothing came back to the window's thread");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void BackgroundWorkRunsOffTheWindowsThreadAndHandsItTheResult()
    {
        using var window = new Control();
        _ = window.Handle; // never shown
        int ui = Environment.CurrentManagedThreadId, worker = 0, at = 0;
        int? result = null;
        Exception? failure = null;
        BackgroundWork.Start(window, () => { worker = Environment.CurrentManagedThreadId; return 42; }, r => (result, at) = (r, Environment.CurrentManagedThreadId),
            ex => failure = ex);
        PumpUntil(() => result is not null || failure is not null);
        Assert.Equal(42, result);
        Assert.Null(failure);
        Assert.NotEqual(ui, worker);
        Assert.Equal(ui, at);
    }

    [Fact]
    public void BackgroundWorkHandsAFailureToTheWindowsThread()
    {
        using var window = new Control();
        _ = window.Handle;
        int ui = Environment.CurrentManagedThreadId, at = 0;
        bool done = false;
        Exception? failure = null;
        BackgroundWork.Start<int>(window, () => throw new InvalidOperationException("the service manager is not available"), _ => done = true,
            ex => (failure, at) = (ex, Environment.CurrentManagedThreadId));
        PumpUntil(() => done || failure is not null);
        Assert.False(done);
        Assert.Equal("the service manager is not available", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Equal(ui, at);
    }

    [Fact]
    public void BackgroundWorkHandsNothingToAWindowThatHasGone()
    {
        bool told = false;
        // Gone before the work ended.
        using var gate = new ManualResetEventSlim();
        var window = new Control();
        _ = window.Handle;
        var work = BackgroundWork.Start(window, () => { gate.Wait(); return 1; }, _ => told = true, _ => told = true);
        window.Dispose();
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(30)));
        // Gone after the result was sent to its thread, before that thread took it (nothing takes this thread's messages meanwhile).
        var other = new Control();
        _ = other.Handle;
        var sent = BackgroundWork.Start(other, () => 1, _ => told = true, _ => told = true);
        Assert.True(SpinWait.SpinUntil(() => sent.IsCompleted, TimeSpan.FromSeconds(30)));
        other.Dispose();
        for (int i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.False(told);
    }

    [Fact]
    public void AFailureOfBackgroundWorkIsObservedEvenWhenItsWindowHasGone()
    {
        string mark = "background failure " + Guid.NewGuid().ToString("N");
        var unobserved = new List<Exception>();
        void Seen(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == mark)) lock (unobserved) unobserved.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += Seen;
        try
        {
            FailAfterTheWindowHasGone(mark);
            // A faulted task nobody looked at is reported when it is collected.
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lock (unobserved) Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= Seen; }
    }

    /// <summary>Work that fails after its window has gone; nothing of it is left reachable from the test once this returns.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailAfterTheWindowHasGone(string mark)
    {
        using var gate = new ManualResetEventSlim();
        var window = new Control();
        _ = window.Handle;
        var work = BackgroundWork.Start<int>(window, () => { gate.Wait(); throw new InvalidOperationException(mark); }, _ => { }, _ => { });
        window.Dispose();
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(30)));
    }

    // ---------------------------------------------------------------- the Services tab's buttons

    private sealed class FakeAgentWindows : IAgentWindows
    {
        public int Prompts, Imports;
        public void OpenPrompt() => Prompts++;
        public void ImportAnswer() { Imports++; throw new InvalidOperationException("the file is gone"); }
    }

    [Fact]
    public void TheServicesTabsButtonsOpenTheWindowsThroughItsGuard()
    {
        using var host = new Panel();
        using var tips = new ToolTip();
        var view = new ServicesTabView(host, tips);
        var agent = new FakeAgentWindows();
        var guarded = new List<string>();
        var failures = new List<string>();
        view.Guard = (what, a) =>
        {
            guarded.Add(what);
            try { a(); }
            catch (InvalidOperationException ex) { failures.Add(ex.Message); }
        };
        view.UseAgent(agent);
        Click(view.Agent);
        Assert.Equal((1, 0), (agent.Prompts, agent.Imports));
        Click(view.ImportAnswer);
        Assert.Equal((1, 1), (agent.Prompts, agent.Imports));
        Assert.Equal(["Decide with your AI agent", "Import answer"], guarded);
        Assert.Equal(["the file is gone"], failures); // reported by the guard, not lost
        view.Page.Dispose();
    }

    /// <summary>
    /// The tab hands its view the app's AI-agent windows, so its two buttons open them (the test above). The tab is made
    /// only by the running app (it needs PoolWatch's engine), and its windows are modal, so this reads what its
    /// constructor does: it makes <see cref="AgentDecisions"/> and calls <see cref="ServicesTabView.UseAgent"/>.
    /// </summary>
    [Fact]
    public void TheServicesTabHandsItsViewTheAgentWindows()
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var il = Assert.Single(typeof(ServicesTab).GetConstructors(Any)).GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(Calls(il, Assert.Single(typeof(AgentDecisions).GetConstructors(Any))), "the Services tab does not make the AI-agent windows");
        Assert.True(Calls(il, typeof(ServicesTabView).GetMethod(nameof(ServicesTabView.UseAgent))!), "the Services tab does not hand its view the AI-agent windows");
    }

    /// <summary>
    /// Apply in the app goes through the CI UI test's refusal (<see cref="AgentApply.UnlessTestRun"/>, tested above) with
    /// whether this run is that test: the windows are made only by the running app, so this reads what it calls.
    /// </summary>
    [Fact]
    public void ApplyInTheAppAsksWhetherThisIsTheCiUiTestFirst()
    {
        var il = typeof(AgentDecisions).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(Calls(il, typeof(SetupContext).GetProperty(nameof(SetupContext.Smoke))!.GetMethod!), "Apply does not read whether this is the CI UI test");
        Assert.True(Calls(il, typeof(AgentApply).GetMethod(nameof(AgentApply.UnlessTestRun))!), "Apply does not go through the CI UI test's refusal");
    }

    /// <summary>Whether <paramref name="il"/> calls <paramref name="method"/> (call, callvirt or newobj, by its token).</summary>
    private static bool Calls(byte[] il, MethodBase method)
    {
        byte[] token = BitConverter.GetBytes(method.MetadataToken);
        for (int i = 0; i + 4 < il.Length; i++)
            if (il[i] is 0x28 or 0x6F or 0x73 && il.AsSpan(i + 1, 4).SequenceEqual(token)) return true;
        return false;
    }
}
