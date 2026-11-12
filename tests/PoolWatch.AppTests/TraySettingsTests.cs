using PoolWatch.Core;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// Settings from the tray, and a Settings choice of what PoolWatch keeps on disk. Setup's Records step says "You can change
/// both later in Settings.", and the Simple view's header has no Settings button (by its approved design), so the tray menu
/// has Settings... for everyone, in both views, right under Open PoolWatch: the same window the Advanced view's button
/// opens, one at a time. And a Settings Save that changes the record mode or the cleanup's days is the owner's choice, as
/// the Records step's is: after an upgrade from 1.0.0-beta.2 (a setup.json without the choice) the Records step no longer
/// opens alone at each start.
/// </summary>
public sealed class TraySettingsTests
{
    private static string Ui(string file) => File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", file));

    /// <summary>
    /// The tray menu: Open PoolWatch, then Settings... (the menu's style for an item that opens a window to fill in, as Run
    /// setup again... and Active changes... have it), then Run setup again..., with nothing between them, in either view
    /// (the menu is the same in both); the item opens Settings through the one opener, and so do the Advanced view's button
    /// and its token-leak offer. Read from the tray's code, as the tray needs the whole app to build.
    /// </summary>
    [Fact]
    public void TheTrayMenuHasSettingsRightUnderOpenPoolWatchAndItOpensTheAdvancedViewsSettings()
    {
        Assert.Equal("Settings...", TrayApp.SettingsMenuItem);
        Assert.EndsWith("...", FirstRunGate.MenuItem);
        string tray = Ui("TrayApp.cs");
        int open = tray.IndexOf("menu.Items.Add(\"Open PoolWatch\", null, (_, _) => ShowWindow());", StringComparison.Ordinal);
        int settings = tray.IndexOf("menu.Items.Add(SettingsMenuItem, null, (_, _) => _actions.Guarded(null, \"Settings\", ShowSettings));", StringComparison.Ordinal);
        int setup = tray.IndexOf("menu.Items.Add(FirstRunGate.MenuItem, null, (_, _) => OpenGuide());", StringComparison.Ordinal);
        Assert.True(open > 0 && open < settings && settings < setup, $"Open PoolWatch {open}, Settings... {settings}, Run setup again... {setup}");
        Assert.Equal(2, Count(tray[open..setup], "menu.Items.Add(")); // nothing between Open PoolWatch and Settings..., nor after it before Run setup again...
        Assert.DoesNotContain("IsAdvanced", tray[open..setup]); // shown in both views
        Assert.Contains("public void ShowSettings() => _settings.Show();", tray);
        Assert.Contains("_settings = new SettingsWindow(Setup.NewSettings, ShowSettingsDialog, Bring);", tray);
        // The Advanced view's button and its token-leak offer go through the same opener; the window makes no Settings of
        // its own to show (its CI screenshots make theirs from the same factory).
        string main = Ui("MainForm.cs");
        Assert.Contains("HeaderButton(\"Settings\", () => _actions.Guarded(this, \"Settings\", _context.Shell.ShowSettings), advancedOnly: true);", main);
        Assert.Contains("_actions.Guarded(this, \"Settings\", _context.Shell.ShowSettings);", main);
        Assert.DoesNotContain("OpenSettings", main);
        Assert.Equal(Count(main, "NewSettings()"), Count(main, "_context.NewSettings()"));
        // Settings over the main window when it shows, else on its own with a taskbar button, centred on the screen.
        Assert.Contains("settings.ShowInTaskbar = owner is null;", tray);
        Assert.Contains("if (owner is null) settings.StartPosition = FormStartPosition.CenterScreen;", tray);
        Assert.Contains("settings.ShowDialog(owner);", tray);
        // Its Save records a record choice in setup.json.
        Assert.Contains("return new(Engine, Analysis, Actions, Live, Shell, offer, Store);", Ui("SetupContext.cs"));
    }

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// One Settings at a time: asked for again while it is open (the tray menu's item, or the Advanced view's button), the one
    /// open is brought to the front and no second one is made; once it closes it is disposed, and the next opening makes a
    /// new one (so it starts from what is saved). A show that throws lets go of it too.
    /// </summary>
    [Fact]
    public void SettingsOpensOneAtATimeAndAskedForAgainBringsTheOneOpenToTheFront()
    {
        var made = new List<Form>();
        var brought = new List<Form>();
        SettingsWindow? window = null;
        bool again = true;
        window = new SettingsWindow(() => { var f = new Form(); made.Add(f); return f; }, f =>
        {
            // While it shows (a dialog: Show returns only once it closes), Settings is asked for twice more.
            Assert.Same(f, window!.Open);
            if (!again) return;
            again = false;
            window.Show();
            window.Show();
        }, brought.Add);
        window.Show();
        Assert.Single(made);
        Assert.Equal([made[0], made[0]], brought);
        Assert.True(made[0].IsDisposed);
        Assert.Null(window.Open);

        window.Show(); // opened again after it closed: a new one
        Assert.Equal(2, made.Count);
        Assert.NotSame(made[0], made[1]);
        Assert.True(made[1].IsDisposed);
        Assert.Equal(2, brought.Count);

        Form? failed = null;
        var failing = new SettingsWindow(() => failed = new Form(), _ => throw new InvalidOperationException("not shown"), _ => { });
        Assert.Throws<InvalidOperationException>(failing.Show);
        Assert.Null(failing.Open);
        Assert.True(failed!.IsDisposed);
    }

    /// <summary>The next start's Records step: whether it opens alone, as the tray app's gate decides it from setup.json read again.</summary>
    private static bool RecordsStepOpensAloneAtTheNextStart(ChangeFixture f)
    {
        var store = new RecommendationStore(f.Paths, f.Log);
        var calls = new List<string>();
        var gate = new FirstRunGate(completed: () => store.GuideCompleted, smoke: false, openSetup: () => calls.Add("setup"), showMain: () => calls.Add("main"),
            switchToAdvanced: _ => { }, switchToSimple: () => { }, rememberView: _ => { }, startUpdates: () => { },
            recordsChosen: () => store.RecordsChosen, openRecords: () => calls.Add("records"));
        gate.Start(showWindow: true);
        return calls.Contains("records");
    }

    /// <summary>1.0.0-beta.2's setup.json: setup completed, and no record choice (its setup had no Records step).</summary>
    private static string Beta2SetupJson(ChangeFixture f)
    {
        Directory.CreateDirectory(f.Paths.DataRoot);
        string file = Path.Combine(f.Paths.DataRoot, RecommendationStore.FileName);
        File.WriteAllText(file, "{\n  \"version\": 1,\n  \"guideCompletedUtc\": \"2026-11-01T09:00:00.0000000Z\",\n  \"dismissed\": {},\n  \"hiddenFeatures\": []\n}");
        return file;
    }

    /// <summary>
    /// After an upgrade from 1.0.0-beta.2 the Records step opens alone at each start until a choice is made. A Settings Save
    /// that changes neither the record mode nor the cleanup (the notices only) is no such choice: the step still opens at
    /// the next start. A Save that changes the record mode is: setup.json records it, and the step does not open again; so
    /// is one that changes the cleanup's days alone. Saved through Save's own writes (<see cref="SettingsEditor.Commit"/>:
    /// settings.json, the settings in effect now, setup.json), as Save makes them once its questions are answered.
    /// </summary>
    [Fact]
    public void ASettingsSaveThatChangesTheRecordModeIsAChoiceSoTheRecordsStepNoLongerOpensAlone()
    {
        using var f = new ChangeFixture();
        string file = Beta2SetupJson(f);
        Assert.True(RecordsStepOpensAloneAtTheNextStart(f));
        var live = f.Live(new Settings(), persist: true);

        // A Save that changes the notices only: no choice of what PoolWatch keeps on disk.
        var editor = new SettingsEditor(analysis: null!, actions: null!, live, shell: null!, new RecommendationStore(f.Paths, f.Log));
        editor.Edit.PoolNotices = PoolNoticeLevel.Off;
        Assert.True(editor.Commit(editor.ToApply()));
        Assert.Equal(PoolNoticeLevel.Off, live.Running.PoolNotices);
        Assert.True(RecordsStepOpensAloneAtTheNextStart(f));
        Assert.DoesNotContain("recordsChosenUtc\": \"", File.ReadAllText(file));

        // A Save that changes the record mode: the owner chose; the Records step does not open at the next start.
        editor = new SettingsEditor(analysis: null!, actions: null!, live, shell: null!, new RecommendationStore(f.Paths, f.Log));
        editor.Edit.RecordMode = RecordMode.Detailed;
        Assert.True(editor.Commit(editor.ToApply()));
        Assert.Equal(RecordMode.Detailed, live.Running.RecordMode);
        Assert.Equal(RecordMode.Detailed, f.Saved().RecordMode);
        Assert.False(RecordsStepOpensAloneAtTheNextStart(f));
        Assert.Contains("\"guideCompletedUtc\": \"2026-11-01T09:00:00.0000000Z\"", File.ReadAllText(file)); // the rest as it was
    }

    [Fact]
    public void ASettingsSaveThatChangesTheCleanupAloneIsAChoiceTooAndOneChangedBackIsNot()
    {
        using var f = new ChangeFixture();
        Beta2SetupJson(f);
        var live = f.Live(new Settings(), persist: true);

        // Changed and changed back before Save: nothing changed here, so nothing chosen.
        var editor = new SettingsEditor(analysis: null!, actions: null!, live, shell: null!, new RecommendationStore(f.Paths, f.Log));
        editor.Edit.RecordMode = RecordMode.Detailed;
        editor.Edit.RecordMode = RecordMode.ReportsOnly;
        editor.Commit(editor.ToApply());
        Assert.True(RecordsStepOpensAloneAtTheNextStart(f));

        // The cleanup's days alone.
        editor = new SettingsEditor(analysis: null!, actions: null!, live, shell: null!, new RecommendationStore(f.Paths, f.Log));
        editor.Edit.RetentionDays = 30;
        editor.Commit(editor.ToApply());
        Assert.Equal(30, live.Running.RetentionDays);
        Assert.False(RecordsStepOpensAloneAtTheNextStart(f));

        // The rule itself: either of the two, never another setting.
        var shown = AtOnceSettings.Of(new Settings());
        Assert.False(shown.ChoosesRecords(shown));
        Assert.True(shown.ChoosesRecords(shown with { Records = RecordMode.Detailed }));
        Assert.True(shown.ChoosesRecords(shown with { RetentionDays = 14 }));
        Assert.False(shown.ChoosesRecords(shown with { PoolNotices = PoolNoticeLevel.Off, CheckForUpdates = !shown.CheckForUpdates, TokenGuard = !shown.TokenGuard }));
    }

    private static string Json(object result) => System.Text.Json.JsonSerializer.Serialize(result);

    /// <summary>
    /// CI's UI run says whether the Settings window the tray opened is centred on its screen from where the window really is
    /// (its bounds against its screen's working area), not from the property that asked for it: centred in the area, or at
    /// its edge along a side it is longer than the area on, as WinForms centres one; not where Windows puts a new window by
    /// default, nor centred one way only, nor at the middle of the screen beside it.
    /// </summary>
    [Fact]
    public void TheTraysSettingsCheckSaysCentredFromWhereTheWindowReallyIs()
    {
        var area = new Rectangle(0, 0, 1920, 1040); // a 1920 x 1080 screen less its taskbar
        Assert.True(TraySettingsCheck.Centred(new Rectangle(560, 220, 800, 600), area));
        Assert.True(TraySettingsCheck.Centred(new Rectangle(561, 221, 799, 599), area)); // odd sizes: the middle to within a pixel
        Assert.False(TraySettingsCheck.Centred(new Rectangle(26, 26, 800, 600), area)); // where Windows puts a new window
        Assert.False(TraySettingsCheck.Centred(new Rectangle(560, 0, 800, 600), area)); // centred across only
        Assert.False(TraySettingsCheck.Centred(new Rectangle(0, 220, 800, 600), area)); // centred down only
        Assert.False(TraySettingsCheck.Centred(new Rectangle(2480, 220, 800, 600), area)); // the middle of the screen to its right
        // A screen to the left of the first, and a window taller than its working area: at the area's top, centred across.
        var left = new Rectangle(-1280, 0, 1280, 680);
        Assert.True(TraySettingsCheck.Centred(new Rectangle(-1040, 0, 800, 760), left));
        Assert.False(TraySettingsCheck.Centred(new Rectangle(-1040, -40, 800, 760), left));

        // The check reads a window that shows: its title, its taskbar button, its place on its screen, and the main window;
        // then closes it. One that does not show yet is waited for.
        foreach (bool centred in new[] { true, false })
        {
            var settings = new Form { Text = "PoolWatch settings", ShowInTaskbar = true, StartPosition = FormStartPosition.Manual };
            var screen = Screen.FromControl(settings).WorkingArea; // gives it a handle; it is never shown
            settings.Bounds = centred ? new Rectangle(screen.X + (screen.Width - 400) / 2, screen.Y + (screen.Height - 300) / 2, 400, 300) : new Rectangle(screen.X + 8, screen.Y + 8, 400, 300);
            Assert.Equal(new Size(400, 300), settings.Size);
            bool shows = false;
            var check = new TraySettingsCheck(() => settings, _ => shows, () => [], () => false);
            Assert.False(check.Tick());
            Assert.False(settings.IsDisposed);
            shows = true;
            Assert.True(check.Tick());
            Assert.True(settings.IsDisposed); // closed again
            Assert.Equal("{\"opened\":true,\"title\":\"PoolWatch settings\",\"showInTaskbar\":true,\"centredOnScreen\":" + (centred ? "true" : "false") +
                         ",\"mainWindowShown\":false,\"simpleView\":true,\"error\":null}", Json(check.Result(simpleView: true)));
        }
        string tray = Ui("TrayApp.cs");
        Assert.Contains("Centred(form.Bounds, Screen.FromControl(form).WorkingArea)", tray);
        Assert.DoesNotContain("StartPosition == FormStartPosition.CenterScreen", tray); // the property says what was asked for, not where the window is
    }

    /// <summary>
    /// When no Settings window shows within 10 s of the click, the check closes every window the click opened (one that
    /// never showed among them), so the click returns, and says why nothing opened; a failure of the click's action is told
    /// to it in place of the box that would wait for a click nobody makes in CI, and it says that; and a click that returns
    /// with neither says so too. So the CI UI run fails its check on the result instead of waiting until the job is stopped.
    /// </summary>
    [Fact]
    public void WhenNoSettingsWindowShowsTheTraysSettingsCheckClosesWhatTheClickOpenedAndSaysWhy()
    {
        var hidden = new Form { Text = "PoolWatch settings" };
        var stray = new Form();
        var check = new TraySettingsCheck(() => hidden, _ => false, () => [hidden, stray], () => true);
        Assert.Equal(10_000, TraySettingsCheck.MaxTicks * TraySettingsCheck.TickMs);
        for (int tick = 1; tick < TraySettingsCheck.MaxTicks; tick++) Assert.False(check.Tick());
        Assert.False(hidden.IsDisposed || stray.IsDisposed);
        Assert.True(check.Tick()); // it gives up
        Assert.True(hidden.IsDisposed && stray.IsDisposed);
        const string nothing = "{\"opened\":false,\"title\":null,\"showInTaskbar\":false,\"centredOnScreen\":false,\"mainWindowShown\":false,\"simpleView\":true,\"error\":";
        Assert.Equal(nothing + "\"no Settings window showed within 10 s\"}", Json(check.Result(simpleView: true)));

        var failed = new TraySettingsCheck(() => null, _ => true, () => [], () => false);
        failed.Failed("Settings failed: no");
        failed.Failed("a later one");
        Assert.Equal(nothing + "\"Settings failed: no\"}", Json(failed.Result(simpleView: true)));
        Assert.Equal(nothing + "\"the click returned before a Settings window showed\"}", Json(new TraySettingsCheck(() => null, _ => true, () => [], () => false).Result(simpleView: true)));
        // A window that showed and was read, and a failure told after it (as it closed, say): not opened, with why.
        var read = new Form { Text = "PoolWatch settings" };
        var both = new TraySettingsCheck(() => read, _ => true, () => [], () => false);
        Assert.True(both.Tick());
        both.Failed("Settings failed: as it closed");
        string json = Json(both.Result(simpleView: false));
        Assert.StartsWith("{\"opened\":false,\"title\":\"PoolWatch settings\",", json);
        Assert.EndsWith(",\"simpleView\":false,\"error\":\"Settings failed: as it closed\"}", json);

        // The tray's hook: for the CI UI run only, a failure of the click told to the check in place of its box, and the
        // timer stopped by the check; only the UI runner calls it.
        string tray = Ui("TrayApp.cs");
        string hook = tray[tray.IndexOf("internal object SettingsFromTrayForTest()", StringComparison.Ordinal)..tray.IndexOf("public void ShowWindow()", StringComparison.Ordinal)];
        Assert.Contains("if (!Setup.Smoke) throw new InvalidOperationException(", hook);
        Assert.Contains("timer.Tick += (_, _) => { if (check.Tick()) timer.Stop(); };", hook);
        Assert.Contains("_actions.FailureTold = check.Failed;", hook);
        Assert.Contains("_actions.FailureTold = null;", hook);
        Assert.Contains("form => form.Visible, () => Application.OpenForms.Cast<Form>().Except(before).ToList()", hook);
        string ui = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        var callers = Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
            !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && File.ReadAllText(file).Contains("SettingsFromTrayForTest()")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["TrayApp.cs", "UiRunner.cs"], callers);
        Assert.Contains("if (FailureTold is { } told) told(message); else MessageBox.Show(owner, message, \"PoolWatch\");", Ui("UiActions.cs"));
        Assert.Equal(2, Count(string.Concat(Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
            !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).Select(File.ReadAllText)), "FailureTold = ")); // set and cleared by the hook alone
    }

    /// <summary>A failure of a guarded action is told in place of its box while the CI UI run's check listens, with what failed.</summary>
    [Fact]
    public void AGuardedActionsFailureIsToldInPlaceOfItsBoxWhileTheUiRunListens()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-guarded-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PoolWatch.Engine.AppPaths(Path.Combine(root, "data"), null, secure: false);
            // Never started: no sampler thread and no trace; the actions only log through it here.
            using var log = new PoolWatch.Engine.Log(Path.Combine(paths.DataRoot, "test.log"));
            using var engine = new PoolWatch.Engine.MonitorEngine(new Settings(), paths, log, verboseCsv: false);
            var actions = new UiActions(engine, analysis: null!, new SynchronizationContext());
            var told = new List<string>();
            actions.FailureTold = told.Add;
            bool ran = false;
            actions.Guarded(null, "Settings", () => ran = true);
            Assert.True(ran);
            Assert.Empty(told);
            actions.Guarded(null, "Settings", () => throw new InvalidOperationException("its window could not be made"));
            Assert.Equal(["Settings failed: its window could not be made"], told);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
