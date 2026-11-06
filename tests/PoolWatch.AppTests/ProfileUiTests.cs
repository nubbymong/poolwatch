using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>The profile row's words, exactly as the mockup has them.</summary>
public class ProfileTextTests
{
    private static readonly ServiceProfileSet Set = new()
    {
        Profiles = [new("Build box", ["SysMain", "MapsBroker"]), new("Quiet evening", ["DiagTrack"])], AtStart = "Build box", DelayMinutes = 3,
    };

    [Fact]
    public void TheLineUnderTheComboSaysWhatTheProfileDoes()
    {
        Assert.Equal("Custom: 4 ticked by hand.", ProfileText.Summary(ProfileChoice.Custom, Set, [], 4, true));
        Assert.Equal("Telemetry only (built-in, read-only): ticks the 6 telemetry services that are not off yet.",
            ProfileText.Summary(new(ProfileKind.BuiltIn, "Telemetry only"), Set, ["a", "b", "c", "d", "e", "f"], 6, true));
        Assert.Equal("Agentic coding PC (built-in, read-only): ticks the 1 service that is not off yet. 3 ticked now.",
            ProfileText.Summary(new(ProfileKind.BuiltIn, "Agentic coding PC"), Set, ["a"], 3, false));
        // It describes the profile, not the PC: how many of its services are on now is said apart.
        Assert.Equal("Build box: keeps 2 services off. Applied when PoolWatch starts, after 3 minutes.",
            ProfileText.Summary(new(ProfileKind.User, "Build box"), Set, ["SysMain", "MapsBroker"], 2, true));
        Assert.Equal("Build box: keeps 2 services off; 1 of them is on now. Applied when PoolWatch starts, after 3 minutes.",
            ProfileText.Summary(new(ProfileKind.User, "Build box"), Set, ["SysMain", "MapsBroker"], 2, true, onNow: 1));
        Assert.Equal("Quiet evening: keeps 1 service off; 9 of them are on now. Applied only when you press Turn off. 14 ticked now.",
            ProfileText.Summary(new(ProfileKind.User, "Quiet evening"), Set, ["DiagTrack"], 14, false, onNow: 9));
        var oneMinute = new ServiceProfileSet { Profiles = Set.Profiles, AtStart = "Build box", DelayMinutes = 1 };
        Assert.Equal("Build box: keeps 2 services off. Applied when PoolWatch starts, after 1 minute.",
            ProfileText.Summary(new(ProfileKind.User, "Build box"), oneMinute, ["SysMain", "MapsBroker"], 2, true));
        // A built-in with nothing left to tick says so.
        Assert.Equal("Telemetry only (built-in, read-only): all of its services are already off.", ProfileText.Summary(new(ProfileKind.BuiltIn, "Telemetry only"), Set, [], 0, true));
    }

    [Fact]
    public void SavingAProfileAppliedAtStartOffersToTurnItsServicesOffNow()
    {
        Assert.Equal("9 of Build box's services are on now. PoolWatch turns them off 3 minutes after it next starts. Turn them off now?", ProfileText.TurnOffNow("Build box", 9, 3));
        Assert.Equal("1 of Build box's services is on now. PoolWatch turns it off 1 minute after it next starts. Turn it off now?", ProfileText.TurnOffNow("Build box", 1, 1));
        // While the run at this start still waits to apply this profile, it turns off what the profile listed then, whatever
        // the answer (ProfileAtStart.Narrowed): one sentence says so.
        Assert.Equal("9 of Build box's services are on now. PoolWatch turns them off 3 minutes after it next starts. It is still waiting to apply Build box " +
                     "from this start, so those it listed then go off when that wait ends, whatever you answer. Turn them off now?",
            ProfileText.TurnOffNow("Build box", 9, 3, waiting: true));
        Assert.Equal("Save the ticked services as a profile, or change one of yours: its services, and whether PoolWatch applies it when it starts.", ProfileText.SaveTip);
    }

    [Fact]
    public void RestoreSaysTheProfileAppliedAtStartWouldTurnTheServiceOffAgain()
    {
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns SysMain off again then. Put back also takes it out of Build box, so it stays on.",
            ProfileText.RestoreKeepsOn(Set, ["SysMain"]));
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns 2 of these services off again then: SysMain, MapsBroker. Put all back also takes " +
                     "them out of Build box, so they stay on.", ProfileText.RestoreKeepsOn(Set, ["Fax", "MapsBroker", "SysMain"], all: true));
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns 1 of these services off again then: SysMain. Put all back also takes it out of " +
                     "Build box, so it stays on.", ProfileText.RestoreKeepsOn(Set, ["SysMain"], all: true));
        Assert.Null(ProfileText.RestoreKeepsOn(Set, ["DiagTrack"])); // only in a profile not applied at start
    }

    [Fact]
    public void TheComboNamesEachEntry()
    {
        Assert.Equal("Telemetry only (built-in)", new ProfileChoice(ProfileKind.BuiltIn, "Telemetry only").ToString());
        Assert.Equal("Build box", new ProfileChoice(ProfileKind.User, "Build box").ToString());
        Assert.Equal("Build box (at start)", new ProfileChoice(ProfileKind.User, "Build box", AtStart: true).ToString());
        Assert.Equal("Custom (tick by hand)", ProfileChoice.Custom.ToString());
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("Telemetry only", "Telemetry only is a built-in profile: pick another name.", true)]
    [InlineData(" agentic CODING pc", "Agentic coding PC is a built-in profile: pick another name.", true)]
    [InlineData("custom (tick by hand)", "Pick another name.", true)]
    [InlineData("build box", "Replaces your saved Build box profile.", false)]
    [InlineData("New one", "", false)]
    [InlineData("Tab\there", "A profile's name cannot hold a control character.", true)]
    public void TheSaveDialogsNameNote(string name, string note, bool error) =>
        Assert.Equal((note, error), ProfileText.SaveNameNote(name, Set));

    [Fact]
    public void TheSaveDialogSaysWhenThereAreFiftyProfilesAlready()
    {
        var fifty = new ServiceProfileSet { Profiles = Enumerable.Range(1, ServiceProfileRules.MaxProfiles).Select(i => new ServiceProfile("P" + i, ["SysMain"])).ToList() };
        Assert.Equal(("There are 50 profiles already, the most PoolWatch keeps: delete one first, or save over one of them.", true),
            ProfileText.SaveNameNote("New one", fifty));
        Assert.Equal(("Replaces your saved P7 profile.", false), ProfileText.SaveNameNote("p7", fifty));
    }

    [Fact]
    public void TheSaveDialogsOtherLines()
    {
        Assert.Equal("Keeps the 14 ticked services off and leaves the others as they are.", ProfileText.SaveWhat(14));
        Assert.Equal("Keeps the 1 ticked service off and leaves the others as they are.", ProfileText.SaveWhat(1));
        Assert.Equal("Tick the services this profile keeps off first.", ProfileText.SaveWhat(0));
        Assert.Equal("", ProfileText.NotKept(0));
        Assert.Equal("1 ticked service is not kept: a profile holds only services PoolWatch recognises.", ProfileText.NotKept(1));
        Assert.Equal("2 ticked services are not kept: a profile holds only services PoolWatch recognises.", ProfileText.NotKept(2));
        Assert.Equal("At each start it turns off any of this profile's services that are on, for example after a Windows update. It never touches a protected service.", ProfileText.AtStartNote);
        Assert.Equal("Saving this stops Build box being applied at start.", ProfileText.StopsOther(true, "Quiet evening", Set));
        Assert.Equal("", ProfileText.StopsOther(false, "Quiet evening", Set));
        Assert.Equal("", ProfileText.StopsOther(true, "build box", Set)); // the one at start itself
    }

    [Theory]
    [InlineData("Quiet evening", "There is already a profile called Quiet evening.")]
    [InlineData("telemetry only", "Telemetry only is a built-in profile: pick another name.")]
    [InlineData("Custom (tick by hand)", "There is already a profile called Custom (tick by hand).")]
    [InlineData("BUILD BOX", "")]  // itself, in another case
    [InlineData("Main box", "")]
    [InlineData("", "")]
    public void TheRenameDialogsNote(string name, string note) => Assert.Equal(note, ProfileText.RenameNote(name, "Build box", Set));

    [Fact]
    public void TheDeleteQuestionAndTheTitles()
    {
        Assert.Equal("Delete the profile Build box? The services stay as they are now; Put back turns back on the ones PoolWatch turned off.", ProfileText.DeleteQuestion("Build box"));
        Assert.Equal("Delete the profile Build box? The services stay as they are now; Put back turns back on the ones PoolWatch turned off. PoolWatch then no " +
                     "longer applies it when it starts.", ProfileText.DeleteQuestion("Build box", atStart: true));
        Assert.Equal(("PoolWatch: save profile", "PoolWatch: rename profile", "PoolWatch: delete profile"), (ProfileText.SaveTitle, ProfileText.RenameTitle, ProfileText.DeleteTitle));
    }
}

/// <summary>The save and rename dialogs as they open and as the name is typed (never shown).</summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public class ProfileDialogTests
{
    private static readonly ServiceProfileSet Set = new()
    {
        Profiles = [new("Build box", ["SysMain"]), new("Quiet evening", ["DiagTrack"])], AtStart = "Quiet evening", DelayMinutes = 3,
    };

    [Fact]
    public void TheSaveDialogSaysWhatItSavesAndRefusesABuiltInsName()
    {
        using var d = new SaveProfileDialog(new SaveRequest("", false, 3, 14, 0, Set));
        Assert.Equal("PoolWatch: save profile", d.Text);
        Assert.Equal("Keeps the 14 ticked services off and leaves the others as they are.", d.What.Text);
        Assert.False(d.Save.Enabled);                         // no name yet
        Assert.False(d.Delay.Enabled);                        // not applied at start
        Assert.Equal(("Apply this profile when PoolWatch starts", 1m, 15m, 3m), (d.AtStart.Text, d.Delay.Minimum, d.Delay.Maximum, d.Delay.Value));
        Assert.Equal(ProfileText.AtStartNote, d.StartNote.Text);
        d.NameBox.Text = "Telemetry only";
        Assert.Equal("Telemetry only is a built-in profile: pick another name.", d.NameNote.Text);
        Assert.NotEqual(SystemColors.GrayText, d.NameNote.ForeColor);   // red: it stops the save
        Assert.False(d.Save.Enabled);
        d.NameBox.Text = "Build box";
        Assert.Equal("Replaces your saved Build box profile.", d.NameNote.Text);
        Assert.Equal(SystemColors.GrayText, d.NameNote.ForeColor);
        Assert.True(d.Save.Enabled);
        Assert.Same(d.Save, d.AcceptButton);
        Assert.Contains(AllTexts(d), t => t == ", after");
        Assert.Contains(AllTexts(d), t => t == "minutes");
        Assert.Contains(AllTexts(d), t => t == "Name:");
        Assert.Contains(AllTexts(d), t => t == "Cancel");
    }

    [Fact]
    public void ApplyingAtStartEnablesTheDelayAndSaysWhichProfileStopsBeingApplied()
    {
        using var d = new SaveProfileDialog(new SaveRequest("Build box", false, 3, 2, 0, Set));
        Assert.Equal("", d.OtherNote.Text);                // an empty note takes no line (it is hidden)
        d.AtStart.Checked = true;
        Assert.True(d.Delay.Enabled);
        Assert.Equal("Saving this stops Quiet evening being applied at start.", d.OtherNote.Text);
        d.Delay.Value = 7;
        d.NameBox.Text = "  Build box ";
        Assert.Equal(new SaveAnswer("Build box", true, 7), d.Answer);
        d.AtStart.Checked = false;
        Assert.Equal("", d.OtherNote.Text);
    }

    [Fact]
    public void WithNothingKeptThereIsNothingToSave()
    {
        using var d = new SaveProfileDialog(new SaveRequest("New", false, 3, 0, 2, Set));
        Assert.Equal("Tick the services this profile keeps off first.", d.What.Text);
        Assert.Equal("2 ticked services are not kept: a profile holds only services PoolWatch recognises.", d.NotKeptNote.Text);
        Assert.False(d.Save.Enabled);
    }

    [Fact]
    public void TheSaveDialogOpensFilledInAndFits()
    {
        using var d = new SaveProfileDialog(new SaveRequest("Quiet evening", true, 9, 1, 0, Set));
        Assert.Equal(("Quiet evening", true, 9m, true), (d.NameBox.Text, d.AtStart.Checked, d.Delay.Value, d.Delay.Enabled));
        Assert.Equal("", d.NotKeptNote.Text);
        d.CreateControl();
        d.PerformLayout();
        var problems = new List<string>();
        MainForm.ClippedControls(d, problems, WindowFitTests.OwnVisible); // its own Visible: the dialog is never shown
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(d.Save.Bottom <= d.ClientSize.Height && d.Save.Parent!.Right <= d.ClientSize.Width);
    }

    [Fact]
    public void TheRenameDialogRefusesAnotherProfilesName()
    {
        using var d = new RenameProfileDialog("Build box", Set);
        Assert.Equal(("PoolWatch: rename profile", "Build box", "Rename"), (d.Text, d.NameBox.Text, d.Rename.Text));
        Assert.True(d.Rename.Enabled);
        d.NameBox.Text = "quiet evening";
        Assert.Equal("There is already a profile called Quiet evening.", d.NameNote.Text);
        Assert.False(d.Rename.Enabled);
        d.NameBox.Text = "Agentic coding PC";
        Assert.Equal("Agentic coding PC is a built-in profile: pick another name.", d.NameNote.Text);
        Assert.False(d.Rename.Enabled);
        d.NameBox.Text = "   ";
        Assert.False(d.Rename.Enabled);
        d.NameBox.Text = " Main box ";
        Assert.Equal(("", true, "Main box"), (d.NameNote.Text, d.Rename.Enabled, d.NewName));
    }

    [Fact]
    public void EveryProfileDialogSharesOneFont()
    {
        using var save = new SaveProfileDialog(new SaveRequest("", false, 3, 1, 0, Set));
        using var rename = new RenameProfileDialog("Build box", Set);
        Assert.Same(save.Font, rename.Font); // a font given to a form is not disposed with it: one for all
        Assert.Same(ProfileDialog.DialogFont, save.Font);
    }

    private static List<string> AllTexts(Control c) => c.Controls.Cast<Control>().SelectMany(x => AllTexts(x).Prepend(x.Text)).ToList();
}

/// <summary>Saving, renaming and deleting profiles: settings.json first, then the running settings (a TEMP data root).</summary>
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    internal (ProfileStore store, LiveSettings live, AppPaths paths) New(bool persist = true)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var live = new LiveSettings(paths, new Core.Settings(), persist, new Log(Path.Combine(paths.DataRoot, "test.log")));
        return (new ProfileStore(live), live, paths);
    }

    private static ServiceProfileSet OnDisk(AppPaths paths) => Core.Settings.Load(paths.SettingsFile).ServiceProfiles;

    [Fact]
    public void ASaveGoesToSettingsJsonAndKeepsOnlyServicesACatalogueOffers()
    {
        var (store, live, paths) = New();
        var followed = new List<Action<Core.Settings>>();
        live.Applied += followed.Add;                       // an open Settings form follows each change
        store.Save(" Build box ", ["SysMain", "SomeVendorSvc", "WinDefend"], atStart: true, 5);
        foreach (var set in new[] { OnDisk(paths), store.Current })
        {
            Assert.Equal("Build box", Assert.Single(set.Profiles).Name);
            Assert.Equal(["SysMain"], set.Profiles[0].Services);
            Assert.Equal(("Build box", 5), (set.AtStart, set.DelayMinutes));
        }
        Assert.Single(followed);
    }

    [Fact]
    public void RenameAndDeleteChangeTheFileAndNoService()
    {
        var (store, _, paths) = New();
        store.Save("A", ["SysMain"], atStart: true, 3);
        store.Rename("A", "Main box");
        Assert.Equal(("Main box", "Main box"), (OnDisk(paths).Profiles.Single().Name, OnDisk(paths).AtStart));
        store.Delete("main box");
        Assert.Empty(OnDisk(paths).Profiles);
        Assert.Null(OnDisk(paths).AtStart);
        Assert.False(File.Exists(Path.Combine(paths.DataRoot, "service-changes.json"))); // nothing was turned on or off
    }

    [Fact]
    public void AFailedWriteChangesNothing()
    {
        var (store, _, paths) = New();
        store.Save("A", ["SysMain"], atStart: false, 3);
        Directory.CreateDirectory(paths.SettingsFile + ".tmp");
        Assert.ThrowsAny<Exception>(() => store.Save("B", ["Fax"], atStart: false, 3));
        Assert.Equal(["A"], store.Current.Profiles.Select(p => p.Name));
        Assert.Equal(["A"], OnDisk(paths).Profiles.Select(p => p.Name));
    }

    [Fact]
    public void ARestoredServiceComesOutOfTheProfileAppliedAtStartOnly()
    {
        var (store, _, paths) = New();
        store.Save("Build box", ["SysMain", "Fax"], atStart: true, 3);
        store.Save("Other", ["Fax"], atStart: false, 3);
        Assert.Equal("Build box", store.KeepOn(["fax", "DiagTrack"]));
        Assert.Equal(["SysMain"], store.Current.Find("Build box")!.Services);
        Assert.Equal(["SysMain"], OnDisk(paths).Find("Build box")!.Services);     // settings.json first
        Assert.Equal(["Fax"], store.Current.Find("Other")!.Services);             // only the one applied at start
        Assert.Null(store.KeepOn(["DiagTrack"]));
    }

    [Fact]
    public void WithAConfigFileOnlyTheRunningSettingsChange()
    {
        var (store, _, paths) = New(persist: false);
        store.Save("A", ["SysMain"], atStart: false, 3);
        Assert.Equal(["A"], store.Current.Profiles.Select(p => p.Name));
        Assert.False(File.Exists(paths.SettingsFile));
    }
}

/// <summary>The profile row on the Services tab: the combo, its line, and Save as profile..., Rename... and Delete.</summary>
public sealed class ProfilePickerTests : IDisposable
{
    private readonly ProfileStoreTests _stores = new();
    private readonly Form _host = new();
    private readonly ServiceListView _list = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _combo = new();
    private readonly Button _save = new(), _rename = new(), _delete = new();
    private readonly Label _summary = new();
    private readonly Dialogs _ask = new();
    private ProfileStore _store = null!;
    private AppPaths _paths = null!;

    private sealed class Dialogs : IProfileDialogs
    {
        public SaveAnswer? Save;
        public SaveRequest? Asked;
        public string? NewName;
        public bool Delete, Yes;
        public bool? DeleteAtStart;
        public readonly List<string> Told = new(), Questions = new();
        public SaveAnswer? AskSave(SaveRequest request) { Asked = request; return Save; }
        public string? AskRename(string current, ServiceProfileSet set) => NewName;
        public bool AskDelete(string name, bool atStart) { DeleteAtStart = atStart; return Delete; }
        public bool AskYesNo(string question, string title) { Questions.Add(title + ": " + question); return Yes; }
        public void Tell(string message) => Told.Add(message);
    }

    public void Dispose() { _host.Dispose(); _stores.Dispose(); }

    private ProfilePicker New(Action<ProfileStore>? setup = null)
    {
        foreach (var c in ServiceRows.Columns) _list.Columns.Add(c, 100);
        _host.Controls.AddRange([_list, _combo]);
        _host.CreateControl();
        (_store, _, _paths) = _stores.New();
        setup?.Invoke(_store);
        var picker = new ProfilePicker(_combo, _save, _rename, _delete, _summary, _list, _store, _ask);
        _list.SetRows(SomeServices.Rows());
        return picker;
    }

    private void Pick(string text) => _combo.SelectedIndex = _combo.Items.Cast<ProfileChoice>().ToList().FindIndex(c => c.ToString() == text);

    [Fact]
    public void TheComboHasTheBuiltInsTheOwnersOwnThenCustom()
    {
        var picker = New(s => { s.Save("Build box", ["SysMain"], false, 3); s.Save("Quiet evening", ["Fax"], false, 3); });
        Assert.Equal(["Telemetry only (built-in)", "Agentic coding PC (built-in)", "Build box", "Quiet evening", "Custom (tick by hand)"],
            _combo.Items.Cast<ProfileChoice>().Select(c => c.ToString()));
        Assert.Equal(ProfileChoice.Custom, picker.Selected);
        Assert.Equal("Custom: 0 ticked by hand.", _summary.Text);
        Assert.False(_rename.Enabled || _delete.Enabled);    // only the owner's own profiles
        Assert.Equal("Profile", _combo.AccessibleName);
    }

    [Fact]
    public void ABuiltInTicksItsServicesThatAreNotOffYet()
    {
        var picker = New();
        Pick("Telemetry only (built-in)");
        Assert.Equal(["DiagTrack", "PcaSvc"], _list.Ticked);   // InventorySvc is off already
        Assert.Equal("Telemetry only (built-in, read-only): ticks the 2 telemetry services that are not off yet.", _summary.Text);
        Assert.Equal("Telemetry only", picker.Via);
        Assert.False(_rename.Enabled || _delete.Enabled);
        _list.SetTicks(["DiagTrack"]);
        Assert.Equal("Telemetry only (built-in, read-only): ticks the 2 telemetry services that are not off yet. 1 ticked now.", _summary.Text);
        Pick("Agentic coding PC (built-in)");
        Assert.Equal(["DiagTrack", "SysMain"], _list.Ticked);   // PcaSvc has no official source, so it is not in it
        Assert.Equal("Agentic coding PC (built-in, read-only): ticks the 2 services that are not off yet.", _summary.Text);
    }

    [Fact]
    public void AnOwnersProfileTicksItsServicesAndCustomLeavesTheTicks()
    {
        var picker = New(s => s.Save("Build box", ["SysMain", "Fax"], true, 4));
        Pick("Build box (at start)");
        Assert.Equal(["SysMain", "Fax"], _list.Ticked);
        // SysMain is Automatic and Fax Manual on this PC: both on now.
        Assert.Equal("Build box: keeps 2 services off; 2 of them are on now. Applied when PoolWatch starts, after 4 minutes.", _summary.Text);
        Assert.True(_rename.Enabled && _delete.Enabled);
        Assert.Equal("profile Build box", picker.Via);
        _list.SetTicks(["SysMain"]);
        Assert.Equal("profile Build box, edited", picker.Via); // ticks changed by hand from the profile's
        _list.SetTicks(["SysMain", "DiagTrack"]);               // as many ticks as the profile's, but not the same
        Assert.EndsWith(" 2 ticked now.", _summary.Text);
        Assert.Equal("profile Build box, edited", picker.Via);
        _list.SetTicks(["SysMain", "Fax"]);
        Pick("Custom (tick by hand)");
        Assert.Equal(["SysMain", "Fax"], _list.Ticked);       // nothing ticked by hand before: Custom leaves the ticks
        Assert.Equal("Custom: 2 ticked by hand.", _summary.Text);
        Assert.Equal("manual", picker.Via);
    }

    [Fact]
    public void TicksMadeByHandComeBackWithCustom()
    {
        var picker = New();
        _list.SetTicks(["Fax", "edgeupdatem"]);                 // by hand, under Custom
        Pick("Telemetry only (built-in)");
        Assert.Equal(["DiagTrack", "PcaSvc"], _list.Ticked);
        Assert.Equal("Telemetry only", picker.Via);
        Pick("Custom (tick by hand)");
        Assert.Equal(["Fax", "edgeupdatem"], _list.Ticked);    // the hand ticks are back
        Assert.Equal("Custom: 2 ticked by hand.", _summary.Text);
        // Looking at two profiles in a row keeps the hand ticks, not the first profile's.
        Pick("Telemetry only (built-in)");
        Pick("Agentic coding PC (built-in)");
        Pick("Custom (tick by hand)");
        Assert.Equal(["Fax", "edgeupdatem"], _list.Ticked);
    }

    [Fact]
    public void AfterADeleteTheTicksMadeByHandUnderCustomAreKeptToo()
    {
        var picker = New(s => s.Save("Build box", ["SysMain"], false, 3));
        Pick("Build box");
        _ask.Delete = true;
        picker.Delete();                                           // back on Custom
        _list.SetTicks(["Fax"]);
        Pick("Telemetry only (built-in)");
        Pick("Custom (tick by hand)");
        Assert.Equal(["Fax"], _list.Ticked);
    }

    [Fact]
    public void SavingWithApplyAtStartOffersToTurnTheServicesThatAreOnOffNowThroughApply()
    {
        var applied = new List<(IReadOnlyList<string> services, string via)>();
        var picker = New();
        picker.ApplyNow = (services, via) => applied.Add((services, via));
        _list.SetTicks(["SysMain", "InventorySvc", "SomeVendorSvc"]); // SysMain is on, InventorySvc off already; the third party is not kept
        _ask.Save = new SaveAnswer("Build box", AtStart: true, 3);
        _ask.Yes = false;
        picker.SaveAs();
        Assert.Equal(["PoolWatch: save profile: 1 of Build box's services is on now. PoolWatch turns it off 3 minutes after it next starts. Turn it off now?"], _ask.Questions);
        Assert.Empty(applied);
        _ask.Yes = true;
        picker.SaveAs();
        // Exactly the service the offer counted, as the profile's change: not the ticks it did not keep, nor one off already.
        var (services, via) = Assert.Single(applied);
        Assert.Equal(["SysMain"], services);
        Assert.Equal("profile Build box", via);
        // Not applied at start, or nothing on now: nothing to offer.
        _ask.Questions.Clear();
        _ask.Save = new SaveAnswer("Quiet", AtStart: false, 3);
        picker.SaveAs();
        _list.SetTicks(["InventorySvc"]);
        _ask.Save = new SaveAnswer("Off already", AtStart: true, 3);
        picker.SaveAs();
        Assert.Empty(_ask.Questions);
        Assert.Single(applied);
        Assert.Equal("Off already (at start)", _combo.Text);
    }

    [Theory]
    [InlineData(" build BOX", true)] // the run at this start still waits to apply this profile (names match as profile names do)
    [InlineData("Other", false)]     // it waits for another profile, which applies nothing of this one now
    [InlineData(null, false)]        // no run waits
    public void TurnThemOffNowSaysSoOnlyWhileTheRunAtThisStartWaitsForThatProfile(string? waitingFor, bool said)
    {
        var picker = New();
        picker.ApplyNow = (_, _) => { };
        picker.WaitingAtStart = () => waitingFor;
        _list.SetTicks(["SysMain"]);
        _ask.Save = new SaveAnswer("Build box", AtStart: true, 3);
        picker.SaveAs();
        string still = said ? " It is still waiting to apply Build box from this start, so those it listed then go off when that wait ends, whatever you answer." : "";
        Assert.Equal([$"PoolWatch: save profile: 1 of Build box's services is on now. PoolWatch turns it off 3 minutes after it next starts.{still} Turn it off now?"],
            _ask.Questions);
    }

    [Fact]
    public void AServiceARestoreTurnedOnIsUnticked()
    {
        var picker = New(s => s.Save("Build box", ["SysMain", "Fax"], true, 3));
        Pick("Build box (at start)");
        Assert.Equal(["SysMain", "Fax"], _list.Ticked);
        // Restore took SysMain out of the profile and turned it on: unticked, so the next Apply leaves it on, and the line
        // under the combo matches the profile again.
        _store.KeepOn(["SysMain"]);
        picker.TurnedOn(["sysmain"]);
        Assert.Equal(["Fax"], _list.Ticked);
        Assert.Equal("Build box: keeps 1 service off; 1 of them is on now. Applied when PoolWatch starts, after 3 minutes.", _summary.Text);
        Assert.Equal("profile Build box", picker.Via);
        // Ticks made by hand under Custom lose it too.
        Pick("Custom (tick by hand)");
        _list.SetTicks(["DiagTrack", "PcaSvc"]);
        Pick("Build box (at start)");
        picker.TurnedOn(["DiagTrack"]);
        Pick("Custom (tick by hand)");
        Assert.Equal(["PcaSvc"], _list.Ticked);
    }

    [Fact]
    public void AProfilesServiceThatIsNotOnThisPcIsNotCountedAsMissingATick()
    {
        New(s => s.Save("Build box", ["SysMain", "MapsBroker"], false, 3)); // MapsBroker is not installed here
        Pick("Build box");
        Assert.Equal(["SysMain"], _list.Ticked);
        Assert.Equal("Build box: keeps 2 services off; 1 of them is on now. Applied only when you press Turn off.", _summary.Text); // MapsBroker is not here
    }

    [Fact]
    public void SaveAsOpensEmptyForANewProfileAndKeepsOnlyServicesItCanHold()
    {
        var picker = New();
        _list.SetTicks(["SysMain", "SomeVendorSvc"]);
        _ask.Save = new SaveAnswer("Build box", true, 5);
        picker.SaveAs();
        Assert.Equal(new SaveRequest("", false, 3, 1, 1, _ask.Asked!.Set), _ask.Asked);  // one of the two is a third party
        var saved = _store.Current.Find("Build box")!;
        Assert.Equal(["SysMain"], saved.Services);
        Assert.True(_store.Current.IsAtStart("Build box"));
        Assert.Equal("Build box", picker.Selected.Name);
        Assert.Equal("Build box: keeps 1 service off; 1 of them is on now. Applied when PoolWatch starts, after 5 minutes. 2 ticked now.", _summary.Text);
        Assert.Equal(["SysMain", "SomeVendorSvc"], _list.Ticked); // saving changes no tick

        picker.SaveAs();                                            // the selected profile of the owner's opens filled in
        Assert.Equal(("Build box", true, 5), (_ask.Asked!.Name, _ask.Asked.AtStart, _ask.Asked.DelayMinutes));
    }

    [Fact]
    public void ACancelledSaveChangesNothing()
    {
        var picker = New();
        _list.SetTicks(["SysMain"]);
        _ask.Save = null;
        picker.SaveAs();
        Assert.Empty(_store.Current.Profiles);
        Assert.Equal(ProfileChoice.Custom, picker.Selected);
    }

    [Fact]
    public void RenameAndDeleteAreForTheOwnersProfileAndDeleteLeavesTheTicks()
    {
        var picker = New(s => s.Save("Build box", ["SysMain"], true, 3));
        Pick("Build box (at start)");
        _ask.NewName = "Main box";
        picker.Rename();
        Assert.Equal(("Main box", true), (picker.Selected.Name, _store.Current.IsAtStart("Main box")));
        Assert.Equal("Main box (at start)", _combo.Text);
        _ask.Delete = false;
        picker.Delete();
        Assert.NotNull(_store.Current.Find("Main box"));            // answered no
        Assert.True(_ask.DeleteAtStart);                            // the question says it is no longer applied at start
        _ask.Delete = true;
        picker.Delete();
        Assert.Empty(_store.Current.Profiles);
        Assert.Null(_store.Current.AtStart);
        Assert.Equal(ProfileChoice.Custom, picker.Selected);
        Assert.Equal(["SysMain"], _list.Ticked);
        Assert.False(File.Exists(Path.Combine(_paths.DataRoot, "service-changes.json")));
        Pick("Telemetry only (built-in)");
        _ask.NewName = "X";
        picker.Rename();                                            // a built-in is never renamed or deleted
        picker.Delete();
        Assert.Empty(_store.Current.Profiles);
        Assert.Empty(_ask.Told);                                    // not even tried
    }

    [Fact]
    public void TheTabOpensOnTheProfileAppliedAtStartOnce()
    {
        var picker = New(s => { s.Save("Other", ["Fax"], false, 3); s.Save("Build box", ["SysMain", "Fax"], true, 3); });
        picker.RowsShown();
        Assert.Equal("Build box", picker.Selected.Name);
        Assert.Equal(["SysMain", "Fax"], _list.Ticked);
        Pick("Custom (tick by hand)");
        _list.SetTicks(["DiagTrack"]);
        picker.RowsShown();                                         // a later read (Refresh, Apply) keeps the owner's choice
        Assert.Equal(ProfileChoice.Custom, picker.Selected);
        Assert.Equal(["DiagTrack"], _list.Ticked);
    }

    [Fact]
    public void AProfileThatCannotBeSavedIsSaidAndNothingChanges()
    {
        var picker = New();
        _list.SetTicks(["SysMain"]);
        Directory.CreateDirectory(_paths.SettingsFile + ".tmp");
        _ask.Save = new SaveAnswer("Build box", false, 3);
        picker.SaveAs();
        Assert.StartsWith("PoolWatch could not save the profile: ", Assert.Single(_ask.Told));
        Assert.Empty(_store.Current.Profiles);
        Assert.Equal(ProfileChoice.Custom, picker.Selected);
    }

    [Fact]
    public void TheRowsActionsGoThroughTheWindowsGuard()
    {
        foreach (var c in ServiceRows.Columns) _list.Columns.Add(c, 100);
        _host.Controls.AddRange([_list, _combo]);
        _host.CreateControl();
        (_store, _, _paths) = _stores.New();
        _store.Save("Build box", ["SysMain"], false, 3);
        var guarded = new List<string>();
        _ = new ProfilePicker(_combo, _save, _rename, _delete, _summary, _list, _store, _ask, (what, a) => { guarded.Add(what); a(); });
        _list.SetRows(SomeServices.Rows());
        Pick("Build box");
        _save.PerformClick();
        _rename.PerformClick();
        _delete.PerformClick();
        Assert.Equal(["Profile", "Save as profile", "Rename profile", "Delete profile"], guarded);
    }
}
