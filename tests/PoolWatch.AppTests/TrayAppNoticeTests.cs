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
    /// A second start passes the right to take the foreground on only once it has found a tray app listening: it opens the
    /// event first and passes the right on right before it sets it, so a start that finds none, and ends in the "already
    /// running" box, passes nothing on. The Windows call itself is not made here, as it would hand on the right of
    /// whatever runs this test: the start gives it to Send as what to run once the event is open, and
    /// <see cref="TheShowEventIsNamedOnlyThroughTheNamespaceAndTheSession"/> holds that from the source.
    /// </summary>
    [Fact]
    public void ASecondStartPassesTheForegroundOnOnlyWhenATrayAppListens()
    {
        int passed = 0;
        using var shown = new SemaphoreSlim(0);
        Assert.False(ShowSignal.Send(_event, () => passed++)); // none listens: the box, and nothing passed on
        Assert.Equal(0, passed);
        using (var listener = ShowSignal.Listen(_event, AdminOnlyAndMe, () => shown.Release(), _ => { }))
        {
            Assert.NotNull(listener);
            int shownWhenPassed = -1;
            Assert.True(ShowSignal.Send(_event, () => { passed++; shownWhenPassed = shown.CurrentCount; }));
            Assert.Equal(1, passed);
            Assert.Equal(0, shownWhenPassed); // passed on before the event was set, so the running app has it as it shows its window
            Assert.True(shown.Wait(TimeSpan.FromSeconds(10)));
        }
        Assert.False(ShowSignal.Send(_event, () => passed++)); // it exited: the box again, and nothing passed on
        Assert.Equal(1, passed);
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
                        "return Send(name, static () => AllowSetForegroundWindow(AsfwAny));", Flat("public static bool Send()"));
        // The right to take the foreground is passed on in that one place, as what Send runs once it has opened the event,
        // right before it sets it (ASecondStartPassesTheForegroundOnOnlyWhenATrayAppListens): never before the open.
        Assert.Single(Regex.Matches(code, @"(?<!extern bool )AllowSetForegroundWindow\("));
        Assert.Contains("if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return false; using (signal) { allowForeground?.Invoke(); return signal.Set(); }",
            Flat("internal static bool Send(string name, Action? allowForeground = null)"));
        Assert.DoesNotContain("NameOf(", code); // only AdminNamespace makes the names
        string names = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "AdminNamespace.cs"));
        Assert.Contains("public string ShowEventName(int sessionId) => NameOf(ShowLeaf + \"-\" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));", names);
        Assert.Equal("Show", AdminNamespace.ShowLeaf);
        Assert.DoesNotContain(AdminNamespace.ShowLeaf, new[] { AdminNamespace.MonitorLeaf, AdminNamespace.ExitLeaf, AdminNamespace.GuardNoticesLeaf });
    }

    /// <summary>
    /// Explorer's "TaskbarCreated" reaches the tray app's own window: Windows' filter lets it through to that window
    /// (asked again, Windows says it was let through already), and when it comes and finds a taskbar window the icon was
    /// not added for yet (here the first message of each watch, which adds once whichever window it finds; later in a
    /// run, another window than the one the icon was last added for, as after Explorer restarted) the icon is added
    /// again, for that message only. An elevated PoolWatch's icon stayed gone after Explorer restarted: that message
    /// never got through. The watches that get a message here are given a taskbar window of the test's own, a number, in
    /// place of this PC's.
    /// </summary>
    [Fact]
    public void TheTaskbarWatchLetsExplorersMessageThroughAndAddsTheIconAgainWhenItComes()
    {
        Assert.NotEqual(0, TaskbarWatch.TaskbarCreated);
        int added = 0;
        IntPtr taskbar = 0x1001;
        using var watch = new TaskbarWatch(() => added++, () => taskbar, () => 0);
        Assert.True(watch.Allowed);
        Assert.True(TaskbarWatch.Allow(watch.Handle, out uint status));
        Assert.Equal(MsgFltInfoAlreadyAllowedForWindow, status);
        taskbar = 0x2002; // the taskbar there as the message comes: the first message of a run adds for whichever it finds
        SendMessageW(watch.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        Assert.Equal(1, added);
        taskbar = 0x3003;
        SendMessageW(watch.Handle, WmUser, 0, 0); // any other message: nothing, whatever the taskbar
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
        int tried = 0;
        using var throwing = new TaskbarWatch(() => { tried++; throw new InvalidOperationException("the icon is gone"); }, () => taskbar, () => 0);
        taskbar = 0x4004;
        SendMessageW(throwing.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        Assert.Equal(1, tried);
        // The tray app's watch looks for Explorer's own taskbar window, by its class, and measures its gap on the PC's timer.
        string code = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TaskbarWatch.cs"));
        Assert.Contains("public TaskbarWatch(Action created) : this(created, FindTaskbar, System.Diagnostics.Stopwatch.GetTimestamp) { }", code);
        Assert.Contains("internal static IntPtr FindTaskbar() => FindWindowW(\"Shell_TrayWnd\", null);", code);
        _ = TaskbarWatch.FindTaskbar(); // whatever this PC has: the call is declared as Windows has it
    }

    /// <summary>
    /// Any program of the session can send "TaskbarCreated" to the tray app's window, one without administrator rights too
    /// (it is the one message let through to it), so a message alone does nothing: the icon is added again, and a line
    /// written in the log, only when the taskbar window is another than the one the icon was last added for, and at most
    /// once every few seconds. The first message of a run adds it once for the taskbar it finds, as the watch has added
    /// it for none before (<see cref="TheTaskbarWatchAddsTheIconOnceAtTheFirstMessageOfARunThatFindsATaskbar"/>). After
    /// that, ten thousand messages with the same taskbar add nothing; a new taskbar adds it once, however
    /// many messages follow; taskbars that keep changing add it once every <see cref="TaskbarWatch.MinGap"/> at most, and
    /// the one the last message found inside that time is added for when the time is over, so an Explorer that restarts
    /// twice in a row keeps its icon; with no taskbar there is nothing to add it to.
    /// </summary>
    [Fact]
    public void TheTaskbarWatchAddsTheIconAgainOnlyForAnotherTaskbarAndAtMostOnceEveryFewSeconds()
    {
        long gap = (long)(TaskbarWatch.MinGap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), now = 0;
        Assert.Equal(TimeSpan.FromSeconds(5), TaskbarWatch.MinGap); // SECURITY.md and DESIGN.md say 5 seconds
        int added = 0;
        IntPtr taskbar = 0x1001;
        using var watch = new TaskbarWatch(() => added++, () => taskbar, () => now);
        void Flood(int messages = 10_000)
        {
            for (int i = 0; i < messages; i++) SendMessageW(watch.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        }

        Flood(1); // the first message of the run: the icon is added once for the taskbar it finds
        Assert.Equal(1, added);
        added = 0; // counted from here: what comes after that first message
        now += 10 * gap;
        Flood(); // the taskbar the icon is on: nothing, however many
        Assert.Equal(0, added);
        Assert.False(watch.LookDue);
        taskbar = 0x2002; // Explorer restarted
        Flood();
        Assert.Equal(1, added);
        Assert.False(watch.LookDue);
        now += 10 * gap;
        Flood(); // still that taskbar, long after: nothing
        Assert.Equal(1, added);
        // Taskbar windows that keep changing (a program can make a window of that class): once, then not again inside the gap.
        for (int i = 0; i < 10_000; i++)
        {
            taskbar = 0x5000 + i;
            SendMessageW(watch.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        }
        Assert.Equal(2, added);
        Assert.True(watch.LookDue); // the last one is looked at again when the gap is over
        now += gap / 2;
        watch.Look(); // its timer, early
        Assert.Equal(2, added);
        Assert.True(watch.LookDue);
        now += gap;
        watch.Look(); // the gap is over: the taskbar there now gets the icon
        Assert.Equal(3, added);
        Assert.False(watch.LookDue);
        Flood();
        Assert.Equal(3, added);
        // An Explorer that restarts twice in a row: the second taskbar gets the icon once the gap is over, with no further message.
        now += 10 * gap;
        taskbar = 0x7001;
        Flood(1);
        taskbar = 0x7002;
        Flood(1);
        Assert.Equal(4, added);
        Assert.True(watch.LookDue);
        now += 2 * gap;
        watch.Look(); // its timer
        Assert.Equal(5, added);
        Assert.False(watch.LookDue);
        // The taskbar the icon was added for is back before the look came due: nothing to do, and no look left.
        now += 10 * gap;
        taskbar = 0x8001;
        Flood(1);
        taskbar = 0x8002;
        Flood(1);
        taskbar = 0x8001;
        Flood(1);
        Assert.Equal(6, added);
        Assert.False(watch.LookDue);
        // No taskbar at all (Explorer is not running): nothing to add the icon to.
        now += 10 * gap;
        taskbar = IntPtr.Zero;
        Flood();
        Assert.Equal(6, added);
        Assert.False(watch.LookDue);
        taskbar = 0x9001; // Explorer is back
        Flood();
        Assert.Equal(7, added);
        // A watch made while there is no taskbar (PoolWatch started before Explorer's): the first taskbar gets the icon at once.
        taskbar = IntPtr.Zero;
        int early = 0;
        using var before = new TaskbarWatch(() => early++, () => taskbar, () => now);
        taskbar = 0xA001;
        SendMessageW(before.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
        Assert.Equal(1, early);
    }

    /// <summary>
    /// The watch takes no taskbar window as one that has the icon until it added the icon for it itself, so the first
    /// "TaskbarCreated" of a run adds the icon once for the taskbar that was there as the watch was made: that taskbar may
    /// have appeared after the tray app made its icon visible (a start that races Explorer's at sign-in), or not have
    /// been ready for the icon, and a watch that took it as one that has the icon passed Explorer's message over, so the
    /// icon was missing for that sign-in. Not again for that taskbar, however many messages follow, at once or long
    /// after; and the watch does not look at the taskbar as it is made (the look comes with a message).
    /// </summary>
    [Fact]
    public void TheTaskbarWatchAddsTheIconOnceAtTheFirstMessageOfARunThatFindsATaskbar()
    {
        long gap = (long)(TaskbarWatch.MinGap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), now = 0;
        int added = 0, looks = 0;
        IntPtr taskbar = 0x1001;
        using var watch = new TaskbarWatch(() => added++, () => { looks++; return taskbar; }, () => now);
        Assert.Equal(0, added); // nothing is added without a message
        Send(watch);
        Assert.Equal(1, added);
        Assert.Equal(1, looks); // one look, for the message: none as the watch was made
        Assert.False(watch.LookDue);
        Send(watch, 10_000); // that taskbar has the icon now: nothing, however many
        Assert.Equal(1, added);
        Assert.False(watch.LookDue);
        now += 10 * gap;
        Send(watch, 10_000); // nor long after
        Assert.Equal(1, added);
        Assert.False(watch.LookDue);
    }

    /// <summary>
    /// A taskbar that appears after the watch was made (PoolWatch started before Explorer's taskbar was there) gets the
    /// icon once, at the first message that finds it: the messages before it, which find no taskbar, add nothing and are
    /// not that first one. After it the rules are the usual ones: nothing for that taskbar, and another taskbar inside
    /// the gap waits for the gap to be over.
    /// </summary>
    [Fact]
    public void TheTaskbarWatchAddsTheIconOnceForATaskbarThatAppearsAfterItWasMade()
    {
        long gap = (long)(TaskbarWatch.MinGap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), now = 0;
        int added = 0;
        IntPtr taskbar = IntPtr.Zero;
        using var watch = new TaskbarWatch(() => added++, () => taskbar, () => now);
        Send(watch, 100); // no taskbar yet: nothing to add the icon to
        Assert.Equal(0, added);
        Assert.False(watch.LookDue);
        taskbar = 0xB001; // Explorer's taskbar is there
        Send(watch);
        Assert.Equal(1, added);
        Send(watch, 10_000);
        Assert.Equal(1, added);
        Assert.False(watch.LookDue);
        taskbar = 0xB002; // another taskbar, inside the gap
        Send(watch);
        Assert.Equal(1, added);
        Assert.True(watch.LookDue);
        now += 2 * gap;
        watch.Look(); // its timer
        Assert.Equal(2, added);
        Assert.False(watch.LookDue);
    }

    /// <summary>
    /// The look the watch makes by itself when the gap is over (its timer's) adds the icon only for the taskbar window
    /// the last message found. An Explorer that starts three times inside one gap: the second taskbar's message comes
    /// inside the gap and waits for it; when the gap is over the third taskbar window is there, but no message has come
    /// for it yet, so nothing has said it is in place. The look adds nothing for it, records nothing and leaves no look
    /// due; the third's own message then adds the icon once. (Before, that look added the icon for whatever window was
    /// there and recorded it, so the third's own message was passed over: had that add not taken, the icon stayed
    /// missing.)
    /// </summary>
    [Fact]
    public void TheTaskbarWatchsLaterLookAddsNothingForATaskbarNoMessageHasComeFor()
    {
        long gap = (long)(TaskbarWatch.MinGap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), now = 0;
        int added = 0;
        IntPtr taskbar = 0xC001;
        using var watch = new TaskbarWatch(() => added++, () => taskbar, () => now);
        Send(watch); // the first taskbar of the run: added at once
        Assert.Equal(1, added);
        now += gap / 2;
        taskbar = 0xC002; // Explorer restarted: the second taskbar's message, inside the gap
        Send(watch);
        Assert.Equal(1, added);
        Assert.True(watch.LookDue);
        taskbar = 0xC003; // Explorer restarted again: the third taskbar window is there, its message has not come yet
        now += gap;
        watch.Look(); // its timer, the gap over
        Assert.Equal(1, added); // nothing for a window no message has come for
        Assert.False(watch.LookDue); // and no look left: that window's own message is waited for
        watch.Look(); // nor would a further look add it
        Assert.Equal(1, added);
        Assert.False(watch.LookDue);
        Send(watch); // the third's own message: the gap is over, so it is added now
        Assert.Equal(2, added);
        Assert.False(watch.LookDue);
        Send(watch, 10_000); // once: that taskbar has the icon
        Assert.Equal(2, added);
        // The same when the third's message comes while the gap is not over yet: the look that message starts adds it.
        now += 10 * gap;
        taskbar = 0xC101;
        Send(watch);
        Assert.Equal(3, added);
        now += gap / 4;
        taskbar = 0xC102; // the second's message, inside the gap
        Send(watch);
        taskbar = 0xC103; // the third is there, with no message yet
        now += gap / 4;
        watch.Look(); // a timer that fires early: nothing for the third, and no look left
        Assert.Equal(3, added);
        Assert.False(watch.LookDue);
        Send(watch); // the third's message, still inside the gap: it waits for the gap
        Assert.Equal(3, added);
        Assert.True(watch.LookDue);
        now += gap;
        watch.Look(); // its timer: the third is the one last heard of now
        Assert.Equal(4, added);
        Assert.False(watch.LookDue);
    }

    /// <summary>
    /// Every message's look records the taskbar window it found as the one last heard of, also inside the gap and while a
    /// look is already due: the second taskbar's message inside the gap, then the third's inside the same gap, and the
    /// look made when the gap is over adds the icon once, for the third, with no further message.
    /// </summary>
    [Fact]
    public void TheTaskbarWatchsLaterLookAddsForTheTaskbarTheLastMessageFound()
    {
        long gap = (long)(TaskbarWatch.MinGap.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), now = 0;
        int added = 0;
        IntPtr taskbar = 0xD001;
        using var watch = new TaskbarWatch(() => added++, () => taskbar, () => now);
        Send(watch); // the first taskbar of the run: added at once
        Assert.Equal(1, added);
        now += gap / 4;
        taskbar = 0xD002; // the second taskbar's message, inside the gap
        Send(watch);
        Assert.Equal(1, added);
        Assert.True(watch.LookDue);
        now += gap / 4;
        taskbar = 0xD003; // the third's message, inside the same gap, a look already due
        Send(watch);
        Assert.Equal(1, added);
        Assert.True(watch.LookDue);
        now += gap;
        watch.Look(); // its timer, the gap over: the third is the one last heard of
        Assert.Equal(2, added);
        Assert.False(watch.LookDue);
        Send(watch, 10_000); // the third has the icon: nothing more for it
        Assert.Equal(2, added);
        Assert.False(watch.LookDue);
        now += 10 * gap;
        taskbar = 0xD002; // and it was the third it was added for, not the second: the second is another taskbar
        Send(watch);
        Assert.Equal(3, added);
    }

    /// <summary>Sends "TaskbarCreated" to a watch's window, as Explorer's broadcast (or any program of the session) does.</summary>
    private static void Send(TaskbarWatch to, int messages = 1)
    {
        for (int i = 0; i < messages; i++) SendMessageW(to.Handle, (uint)TaskbarWatch.TaskbarCreated, 0, 0);
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
        Assert.Contains("private void AddIconAgain() { _engine.Log.Info(\"a message said the taskbar is in place: the tray icon is added to it again\"); " +
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
