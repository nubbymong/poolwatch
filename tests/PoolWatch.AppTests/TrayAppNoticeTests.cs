extern alias launcher;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using PoolWatch.Engine;
using PoolWatch.Headless;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The tray's notices and the way back to its window, after the owner's report on beta.2: a click on a notice opened
/// nothing, a start of PoolWatch while it ran said "already running", each toast showed "PoolWatch" twice, and the
/// "..." menu on a toast did nothing. Here: a second start shows the running window (names of the test's own in the
/// session's local namespace, never the real ones a PoolWatch on this PC listens on), the tray icon is added again when
/// Explorer's taskbar comes back, the setup registers the notices' AppUserModelID, and the tray app's wiring (it needs
/// the running app, so its lines are held). What a click on a notice opens: ProfileAtStartTests; the notices' words and
/// which the Notifications choice shows: PoolCausesTests; the choice in Settings: SimpleSettingsTests.
/// </summary>
public sealed class TrayAppNoticeTests
{
    private readonly string _event = @"Local\PoolWatch-Test-Show-" + Guid.NewGuid().ToString("N");

    /// <summary>The admin-only descriptor plus the account running the tests, which may not be an administrator.</summary>
    private static string AdminOnlyAndMe => AdminNamespace.AdminOnly + $"(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})";

    /// <summary>
    /// The running tray app listens on its session's event, and every start after it sets that event, each time, not only
    /// the first; an event that exists already is not the app's own and is not used; and with no tray app listening (none
    /// runs, or it has exited) a start finds none, so it says "already running" as before.
    /// </summary>
    [Fact]
    public void ASecondStartShowsTheRunningWindowEachTimeAndFindsNoneOnceItExited()
    {
        var log = new List<string>();
        using var shown = new SemaphoreSlim(0);
        Assert.False(ShowSignal.Send(_event)); // none listens yet
        using (var listener = ShowSignal.Listen(_event, AdminOnlyAndMe, () => shown.Release(), s => { lock (log) log.Add(s); }))
        {
            Assert.NotNull(listener);
            Assert.Null(ShowSignal.Listen(_event, AdminOnlyAndMe, () => throw new InvalidOperationException("not this one"), s => { lock (log) log.Add(s); }));
            for (int start = 1; start <= 3; start++)
            {
                Assert.True(ShowSignal.Send(_event), $"start {start} found no tray app");
                Assert.True(shown.Wait(TimeSpan.FromSeconds(10)), $"start {start} showed no window");
            }
        }
        Assert.False(ShowSignal.Send(_event)); // it exited: a start says "already running"
        lock (log)
        {
            Assert.Equal(3, log.Count(l => l == "show signal: PoolWatch was started again, so its window is shown"));
            Assert.Contains(log, l => l.StartsWith($"show signal: not used, {_event} already exists and PoolWatch did not create it", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Once the tray app stopped listening its event is gone, every time, so a start just after says "already running"
    /// rather than set an event nothing listens on (the pool's wait thread let go of it only when it next looked: 2 of
    /// 2,000 outlived their listener on the maintainer's PC, and the test above failed so once in a gate run).
    /// </summary>
    [Fact]
    public void AStoppedListenerLeavesNoShowEventBehind()
    {
        int outlived = 0;
        for (int i = 0; i < 2000; i++)
        {
            string name = _event + "-" + i;
            var listener = ShowSignal.Listen(name, AdminOnlyAndMe, () => { }, _ => { });
            Assert.NotNull(listener);
            listener!.Dispose();
            if (ShowSignal.Send(name)) outlived++;
        }
        Assert.Equal(0, outlived);
    }

    /// <summary>
    /// A second tray app started to show its window asks the running one and says nothing more when it was asked; it says
    /// "already running" only when no tray app of its session listens. One started with --tray (the logon task) opens no
    /// window and asks for none; a headless run says so on its console, as before.
    /// </summary>
    [Fact]
    public void ASecondTrayAppBringsTheRunningWindowForwardInsteadOfSayingAlreadyRunning()
    {
        var said = new List<string>();
        int asked = 0;
        void Start(string[] args, bool listens) => Program.AlreadyRunning(CommandLineOptions.Parse(args), () => { asked++; return listens; },
            s => said.Add("console: " + s), s => said.Add("box: " + s));

        Start([], listens: true); // from the Start menu, or by Windows for a click on a notice in the notification centre
        Assert.Equal(1, asked);
        Assert.Empty(said);
        Start([], listens: false); // a headless run holds the mutex, or the tray app runs in another session
        Assert.Equal(["box: PoolWatch is already running (see the tray)."], said);
        said.Clear();
        asked = 0;
        Start(["--tray"], listens: true);
        Assert.Equal(0, asked);
        Assert.Equal(["box: " + Program.AlreadyRunningText], said);
        said.Clear();
        Start(["--headless"], listens: true);
        Assert.Equal(0, asked);
        Assert.Equal(["console: PoolWatch is already running."], said);
        // Program.Main passes the real ask, the console and the box.
        string program = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Program.cs")));
        Assert.Contains("() => AlreadyRunning(opt, Engine.ShowSignal.Send, Console.Error.WriteLine, text => MessageBox.Show(text, \"PoolWatch\")),", program);
    }

    /// <summary>
    /// The show event is named only through PoolWatch's private namespace, beside its other names there
    /// (<see cref="AdminNamespace.ShowEventName"/>), with this process's session: never an open name, and never one without
    /// the session, so a start in one session never shows a window in another. The methods that name it are held whole (a
    /// fallback to an open name would fail here), and its name is the leaf, a dash and the session, a leaf of its own.
    /// </summary>
    [Fact]
    public void TheShowEventIsNamedOnlyThroughTheNamespaceAndTheSession()
    {
        string code = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "ShowSignal.cs"));
        Assert.DoesNotMatch(@"@?""(Global|Local|Session)\\", code);
        string Flat(string signature) => Regex.Replace(MainWindowLayoutTests.Block(code, signature), @"\s+", " ").Trim();
        Assert.Contains("try { name = AdminNamespace.Shared.ShowEventName(TokenLeakGuard.OwnSession()); } catch (Exception ex) { log(\"show signal: not used (\" + ex.Message + \")\"); return null; } " +
                        "return Listen(name, AdminNamespace.AdminOnly, onShow, log);", Flat("public static IDisposable? Listen(Action onShow, Action<string> log)"));
        Assert.Contains("try { name = AdminNamespace.Shared.ShowEventName(TokenLeakGuard.OwnSession()); } catch (Exception) { return false; } " +
                        "AllowSetForegroundWindow(AsfwAny); return Send(name);", Flat("public static bool Send()"));
        Assert.DoesNotContain("NameOf(", code); // only AdminNamespace makes the names
        string names = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "AdminNamespace.cs"));
        Assert.Contains("public string ShowEventName(int sessionId) => NameOf(ShowLeaf + \"-\" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));", names);
        Assert.Equal("Show", AdminNamespace.ShowLeaf);
        Assert.DoesNotContain(AdminNamespace.ShowLeaf, new[] { AdminNamespace.MonitorLeaf, AdminNamespace.ExitLeaf, AdminNamespace.GuardNoticesLeaf });
    }

    /// <summary>
    /// Explorer's "TaskbarCreated" reaches the tray app's own window: Windows' filter lets it through to that window
    /// (asked again, Windows says it was let through already), and when it comes the icon is added again, for that message
    /// only. An elevated PoolWatch's icon stayed gone after Explorer restarted: that message never got through.
    /// </summary>
    [Fact]
    public void TheTaskbarWatchLetsExplorersMessageThroughAndAddsTheIconAgainWhenItComes()
    {
        Assert.NotEqual(0, TaskbarWatch.TaskbarCreated);
        int added = 0;
        using var watch = new TaskbarWatch(() => added++);
        Assert.True(watch.Allowed);
        Assert.True(TaskbarWatch.Allow(watch.Handle, out uint status));
        Assert.Equal(MsgFltInfoAlreadyAllowedForWindow, status);
        SendMessageW(watch.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        Assert.Equal(1, added);
        SendMessageW(watch.Handle, WmUser, 0, 0); // any other message: nothing
        Assert.Equal(1, added);
        // A window of this process that was never let through: Windows had nothing allowed for it.
        var plain = new NativeWindow();
        plain.CreateHandle(new CreateParams());
        try
        {
            Assert.True(TaskbarWatch.Allow(plain.Handle, out uint first));
            Assert.Equal(0u, first);
        }
        finally { plain.DestroyHandle(); }
        // A message whose action throws never takes the UI thread down.
        using var throwing = new TaskbarWatch(() => throw new InvalidOperationException("the icon is gone"));
        SendMessageW(throwing.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
    }

    /// <summary>
    /// The tray app's wiring for its notices, its window and its icon, each a whole line of its own (one commented out does
    /// not match): a click on a notice goes to <see cref="BalloonClick"/>, which opens the main window for a notice with
    /// nothing of its own, and so does each close; an incident's notice is one with nothing of its own, so it opens the
    /// main window, and it comes as the incident opens or, under "Only when it needs attention", at the refresh of the icon
    /// once the incident turned red (<see cref="IncidentNotices"/>, under the history lock); no notice's title is
    /// "PoolWatch" (the toast names the app above it), and none is longer than a notice shows; a second start shows the
    /// window; the icon is added again when Explorer's taskbar comes back; and each of these stops as the app exits.
    /// The main window comes to the front when it is shown.
    /// </summary>
    [Fact]
    public void TheTrayAppOpensItsWindowFromEveryNoticeAndASecondStartAndKeepsItsIcon()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayApp.cs"));
        foreach (string line in new[]
        {
            "_balloonClick = new BalloonClick(ShowWindow, engine.Log.Info);",
            "_icon.BalloonTipClicked += (_, _) => _balloonClick.Clicked();",
            "_icon.BalloonTipClosed += (_, _) => _balloonClick.Closed();",
            "_notices = new IncidentNotices(analysis.Knowledge.Lookup, () => engine.Services, engine.Log.Info);",
            "if (_notices.Opened(inc, _live.Running.PoolNotices) is { } notice) _ui.Post(_ => Balloon(notice.Title, notice.Text), null);",
            "foreach (var notice in NoticesTurnedRed()) Balloon(notice.Title, notice.Text);",
            "lock (_analysis.History.SyncRoot) return _notices.TurnedRed(_analysis.Incidents.Open, _live.Running.PoolNotices);",
            "_showSignal = smoke ? null : ShowSignal.Listen(() => _ui.Post(_ => ShowWindow(), null), engine.Log.Info);",
            "_taskbar = new TaskbarWatch(AddIconAgain);",
            "_showSignal?.Dispose(); // and a start from now on says \"already running\"",
            "_taskbar.Dispose(); // before the icon goes, so it is never added again",
            "title = NoticeTitle(title);",
            "_balloonClick.Shown(title, onClick);",
            "_icon.BalloonTipTitle = title;",
        })
            Assert.Matches(new Regex(@"(?m)^[ \t]*" + Regex.Escape(line) + @"[ \t]*\r?$"), source);
        string flat = UninstallerTests.OneLine(source);
        Assert.Contains("private void AddIconAgain() { _engine.Log.Info(\"Explorer's taskbar was created again: the tray icon is added to it again\"); " +
                        "_icon.Visible = false; _icon.Visible = true; }", flat);
        Assert.Contains("private void ShowMain() { Form.Show(); if (Form.WindowState == FormWindowState.Minimized) Form.WindowState = FormWindowState.Normal; Form.Activate(); }", flat);
        // Every notice has a title of its own, and none is "PoolWatch".
        var calls = Regex.Matches(source, @"\bBalloon\((?<title>[^,]+),").Select(m => m.Groups["title"].Value.Trim()).ToList();
        Assert.True(calls.Count >= 9, string.Join(" | ", calls));
        Assert.DoesNotContain(calls, t => t.Contains("\"PoolWatch\"", StringComparison.Ordinal));
        Assert.DoesNotContain("BalloonTipTitle = \"PoolWatch\"", source);
        foreach (string title in new[] { "\"Capture now\"", "\"Experiment\"", "\"Timeline mark\"", "TokenLeakFoundTitle", "ChangesTitle", "notice.Title" })
            Assert.Contains("Balloon(" + title + ", ", source);
        Assert.Equal("Windows token leak found", TrayApp.TokenLeakFoundTitle);
        Assert.Equal("Service profile at start", TrayApp.ChangesTitle);
        // A title too long for a notice is cut where it still shows, marked.
        Assert.Equal("Capture now", TrayApp.NoticeTitle("Capture now"));
        string cut = TrayApp.NoticeTitle(new string('x', 40) + " " + new string('y', 40));
        Assert.True(cut.Length <= PoolCauses.MaxNoticeTitle && cut.EndsWith("...", StringComparison.Ordinal), cut);
        string whole = new('z', PoolCauses.MaxNoticeTitle);
        Assert.Equal(whole, TrayApp.NoticeTitle(whole));
    }

    /// <summary>
    /// The setup registers the AppUserModelID PoolWatch's notices go under (the launcher's and the Start menu entry's) in
    /// Windows' own list, with the name and icon of the notices, as Microsoft documents it for an unpackaged app (the icon a
    /// PNG); the uninstaller removes it. The icon is the emblem as a PNG that build.ps1 stages with the other files that
    /// ship beside the launcher, and the setup installs there, named on its own line (and so left out of the wildcard). On
    /// the owner's PC that entry was missing while the Start menu entry carried the ID. The installer's end-to-end test
    /// checks the file, the entry's values after the install and that the entry is gone after the uninstall, each check
    /// with its marker (CheckMarkerTests).
    /// </summary>
    [Fact]
    public void TheSetupRegistersTheNoticesAppUserModelIdWithItsNameAndIcon()
    {
        string root = TrayIconsTests.RepoRoot();
        string[] lines = File.ReadAllText(Path.Combine(root, "installer", "PoolWatch.iss")).Replace("\r\n", "\n").Split('\n');
        string id = launcher::PoolWatch.Launcher.Native.AppUserModelId;
        string key = @"Software\Classes\AppUserModelId\" + id;
        int section = Array.IndexOf(lines, "[Registry]");
        Assert.True(section >= 0, "the setup has no [Registry] section");
        var entries = lines[(section + 1)..].TakeWhile(l => !l.StartsWith('[')).Where(l => !l.StartsWith(';') && l.Length > 0).ToList();
        Assert.Equal([
            $@"Root: HKLM; Subkey: ""{key}""; ValueType: string; ValueName: ""DisplayName""; ValueData: ""PoolWatch""; Flags: uninsdeletekey",
            $@"Root: HKLM; Subkey: ""{key}""; ValueType: string; ValueName: ""IconUri""; ValueData: ""{{app}}\poolwatch.png""; Flags: uninsdeletekey",
        ], entries);
        Assert.Single(lines, l => l == "[Registry]");
        Assert.Contains(@"Source: ""{#PublishDir}\poolwatch.png""; DestDir: ""{code:ProgramDir}""; Flags: ignoreversion; BeforeInstall: Shipping", lines);
        Assert.Single(lines, l => l.StartsWith(@"Source: ""{#PublishDir}\*""", StringComparison.Ordinal) && l.Contains(@",\poolwatch.png""; ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("poolwatch.ico", StringComparison.Ordinal) && !l.StartsWith("SetupIconFile=", StringComparison.Ordinal));
        Assert.Contains($@"Name: ""{{autoprograms}}\PoolWatch""; Filename: ""{{app}}\PoolWatch.exe""; AppUserModelID: ""{id}""", lines);
        // build.ps1 stages it under that name from the emblem the setup's pages show, a PNG.
        Assert.Contains(@"Copy-Item -LiteralPath ""$PSScriptRoot\poolwatch-small-124x124.png"" -Destination (Join-Path $stage 'poolwatch.png')",
            File.ReadAllText(Path.Combine(root, "installer", "build.ps1")));
        byte[] png = File.ReadAllBytes(Path.Combine(root, "installer", "poolwatch-small-124x124.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        // The end-to-end test: the file is installed, the entry has these values after the install, and it is gone after
        // the uninstall.
        string e2e = File.ReadAllText(Path.Combine(root, "ci", "installer-e2e.ps1"));
        Assert.Contains("$aumidKey = 'HKLM:\\SOFTWARE\\Classes\\AppUserModelId\\" + id + "'", e2e);
        Assert.Contains("'THIRD-PARTY-NOTICES.md', 'poolwatch.png', ", e2e);
        Assert.True(ScriptCode.CheckThenMarker(e2e, "Assert-True ($aumid.DisplayName -eq 'PoolWatch' -and $aumid.IconUri -eq (Join-Path $app 'poolwatch.png') -and (Test-Path -LiteralPath $aumid.IconUri)) \"",
            "installer-notices-app-id-registered"), "the end-to-end test does not check the entry after the install, with its marker next");
        Assert.True(ScriptCode.CheckThenMarker(e2e, "Assert-True (-not (Test-Path -LiteralPath $aumidKey)) \"", "installer-notices-app-id-removed"),
            "the end-to-end test does not check that the uninstall removed the entry, with its marker next");
        string[] statements = [.. ScriptCode.StatementsOf(e2e)!];
        int install = Array.IndexOf(statements, "Install 'install-1'"), registered = Array.IndexOf(statements, ScriptCode.Marker("installer-notices-app-id-registered"));
        int uninstall = Array.IndexOf(statements, "Uninstall 'uninstall-1'"), removed = Array.IndexOf(statements, ScriptCode.Marker("installer-notices-app-id-removed"));
        Assert.True(install >= 0 && install < registered && registered < uninstall && uninstall < removed, $"{install} {registered} {uninstall} {removed}");
        Assert.DoesNotContain(statements[(install + 1)..registered], s => s.StartsWith("Uninstall ", StringComparison.Ordinal) || s.StartsWith("Install ", StringComparison.Ordinal));
        Assert.DoesNotContain(statements[(uninstall + 1)..removed], s => s.StartsWith("Install ", StringComparison.Ordinal));
    }

    private const uint WmUser = 0x0400, MsgFltInfoAlreadyAllowedForWindow = 1;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, nint wParam, nint lParam);
}
