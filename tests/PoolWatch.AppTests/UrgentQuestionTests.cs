using PoolWatch.Core;
using PoolWatch.Engine;
using PoolWatch.Ui;
using PoolWatch.Ui.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// After an upgrade from a version without the urgent clear, it is off until its owner chooses, and PoolWatch asks once
/// at a start while automatic clearing is on: a small window, as the token-leak guard's question and the non-paged pool's
/// notice are, with the setup switch's words and three answers: Turn on (green), Leave off and Decide later. Settings go
/// to a settings.json in TEMP; the window is built, never shown.
/// </summary>
public sealed class UrgentQuestionTests : IDisposable
{
    private readonly ChangeFixture _f = new();
    private readonly List<string> _log = [];
    private readonly List<UrgentQuestionWindow> _shown = [];

    public void Dispose()
    {
        foreach (var window in _shown) window.Dispose();
        _f.Dispose();
    }

    /// <summary>
    /// settings.json as a version without the urgent clear saved it (1.0.0, the current release, has every other setting
    /// of this build's file but the question's mark, which is written only while it is set), with automatic clearing on
    /// (every hour, or at 8 GB) or off: an upgrade.
    /// </summary>
    internal static string ReleaseFile(bool clearing)
    {
        string json = new Settings { AutoClearEnabled = clearing }.ToJson();
        const string Key = "\"autoClearUrgent\": true,";
        Assert.Contains(Key, json);
        Assert.DoesNotContain("autoClearUrgentAsk", json);
        return json.Replace(Key, "", StringComparison.Ordinal);
    }

    /// <summary>Writes <see cref="ReleaseFile"/> as this test's settings.json.</summary>
    private void Upgrade(bool clearing)
    {
        Directory.CreateDirectory(_f.Paths.DataRoot);
        File.WriteAllText(_f.Paths.SettingsFile, ReleaseFile(clearing));
    }

    /// <summary>The question as the tray app makes it, over settings read from this test's settings.json as PoolWatch starts.</summary>
    private UrgentQuestion Question(out LiveSettings live)
    {
        live = _f.Live(_f.Saved());
        return new UrgentQuestion(live, _log.Add, _shown.Add);
    }

    [Fact]
    public void AnUpgradeWithAutomaticClearingOnIsAskedOnceAtAStart()
    {
        Upgrade(clearing: true);
        var question = Question(out var live);
        Assert.True(live.Running.UrgentClearToAsk());
        // Never in the CI UI test, and not while setup was never completed: its Standby cache step has the switch.
        Assert.False(question.AtStart(setupCompleted: true, smoke: true));
        Assert.False(question.AtStart(setupCompleted: false, smoke: false));
        Assert.Empty(_shown);
        Assert.True(question.AtStart(setupCompleted: true, smoke: false));
        var window = Assert.Single(_shown);
        Assert.Same(window, question.Open);
        Assert.Contains(_log, l => l.StartsWith("the urgent clear's question after the upgrade is shown", StringComparison.Ordinal));
        // Once per start at most, however it was answered.
        Assert.False(question.AtStart(setupCompleted: true, smoke: false));
        question.Answered(UrgentAnswer.Later);
        Assert.False(question.AtStart(setupCompleted: true, smoke: false));
        Assert.Single(_shown);
    }

    /// <summary>
    /// An upgrade with automatic clearing off is not asked (the urgent clear acts only while that is on, and every place
    /// that turns it on offers the urgent clear); a file that already has the urgent clear never is; nor is a new install.
    /// </summary>
    [Fact]
    public void NoOneElseIsAsked()
    {
        Upgrade(clearing: false);
        Assert.False(Question(out _).AtStart(setupCompleted: true, smoke: false));
        foreach (bool urgent in new[] { true, false })
        {
            new Settings { AutoClearEnabled = true, AutoClearUrgent = urgent }.Save(_f.Paths.SettingsFile);
            Assert.False(Question(out _).AtStart(setupCompleted: true, smoke: false));
        }
        File.Delete(_f.Paths.SettingsFile);
        Assert.False(Question(out _).AtStart(setupCompleted: true, smoke: false));
        Assert.Empty(_shown);
        Assert.Empty(_log);
    }

    /// <summary>
    /// Each answer writes exactly what it says, at once and in settings.json: Turn on turns the urgent clear on, Leave off
    /// keeps it off, and neither is asked again; Decide later, and a close without an answer, write nothing, so it stays
    /// off and the next start asks again. Automatic clearing stays as it was. The log says the answer.
    /// </summary>
    [Theory]
    [InlineData("Turn on")]
    [InlineData("Leave off")]
    [InlineData("Decide later")]
    [InlineData("closed")]
    public void EachAnswerWritesExactlyWhatItSays(string answer)
    {
        Upgrade(clearing: true);
        string before = File.ReadAllText(_f.Paths.SettingsFile);
        var question = Question(out var live);
        Assert.True(question.AtStart(setupCompleted: true, smoke: false));
        UrgentAnswer? given = answer switch
        {
            "Turn on" => UrgentAnswer.TurnOn,
            "Leave off" => UrgentAnswer.LeaveOff,
            "Decide later" => UrgentAnswer.Later,
            _ => null,
        };
        question.Answered(given);
        var saved = _f.Saved();
        bool askedAtTheNextStart = Question(out _).AtStart(setupCompleted: true, smoke: false);
        var (urgent, toChoose) = given switch
        {
            UrgentAnswer.TurnOn => (true, false),
            UrgentAnswer.LeaveOff => (false, false),
            _ => (false, true),
        };
        Assert.Equal((urgent, toChoose), (live.Running.AutoClearUrgent, live.Running.AutoClearUrgentAsk));
        Assert.Equal((urgent, toChoose), (saved.AutoClearUrgent, saved.AutoClearUrgentAsk));
        Assert.Equal(toChoose, askedAtTheNextStart);
        if (toChoose) Assert.Equal(before, File.ReadAllText(_f.Paths.SettingsFile)); // nothing written
        Assert.Equal((true, true), (live.Running.AutoClearEnabled, saved.AutoClearEnabled));
        string said = given switch
        {
            UrgentAnswer.TurnOn => "the urgent clear's question after the upgrade: Turn on (the urgent clear is on now)",
            UrgentAnswer.LeaveOff => "the urgent clear's question after the upgrade: Leave off (it stays off, and is not asked about again)",
            UrgentAnswer.Later => "the urgent clear's question after the upgrade: Decide later (it stays off until it is chosen, and is asked about again at the next start)",
            _ => "the urgent clear's question after the upgrade: closed without an answer, as Decide later (it stays off until it is chosen, and is asked about again at the next start)",
        };
        Assert.Contains(said, _log);
        Assert.Equal(said, UrgentQuestion.LogLine(given));
    }

    /// <summary>
    /// The question has the title and the words of setup's switch (the switch's row, and the step's line on what a clear
    /// is like), and three answers, left to right: Turn on, in green, the safe choice for the PC; Leave off; Decide later,
    /// which Esc gives once it has the focus. Tab goes through them left to right, as they are read, and once they can be
    /// pressed Decide later has the focus, so Enter presses Decide later until the focus is moved. It is a window as the
    /// non-paged pool's notice is: above other windows from the start, never taking the focus as it shows (it comes at a
    /// start, perhaps at sign-in while you type), with a taskbar button, on the screen that holds the tray, in the one
    /// shared font. An answer is given once; a close with none is Decide later.
    /// </summary>
    [Fact]
    public void TheQuestionHasTheSwitchsWordsAndTurnOnIsGreen()
    {
        UrgentAnswer? got = UrgentAnswer.LeaveOff;
        int answers = 0;
        void Answered(UrgentAnswer? a) { got = a; answers++; }
        long now = 0;
        using var window = new UrgentQuestionWindow(5L << 30, Answered, () => now);
        Assert.Equal("PoolWatch: " + SetupText.UrgentTitle, window.Text);
        Assert.Equal("Clear at once if the non-paged pool is over its limit and still rising", SetupText.UrgentTitle);
        Assert.Equal(SetupText.UrgentRow(5L << 30) + "." + Environment.NewLine + SetupText.Stutter, window.Words);
        Assert.Equal(["Turn on", "Leave off", "Decide later"], window.Buttons);
        Assert.Equal([UrgentQuestion.TurnOnLabel, UrgentQuestion.LeaveOffLabel, UrgentQuestion.LaterLabel], window.Buttons);
        var turnOn = window.ButtonFor(UrgentAnswer.TurnOn);
        if (!SystemInformation.HighContrast) Assert.Equal((UiColors.Good, Color.White), (turnOn.BackColor, turnOn.ForeColor));
        Assert.Equal(UrgentQuestion.LaterLabel, Assert.IsType<Button>(window.CancelButton).Text);
        Assert.Null(window.AcceptButton); // no default button of its own: Enter presses the button with the focus
        Assert.Equal([0, 1, 2], new[] { UrgentAnswer.TurnOn, UrgentAnswer.LeaveOff, UrgentAnswer.Later }.Select(a => window.ButtonFor(a).TabIndex));
        Assert.Equal(NoticeWindow.TopMostStyle, window.ExtendedStyle & NoticeWindow.TopMostStyle);
        Assert.False(window.TopMost);
        Assert.False(window.TakesFocusAsItShows);
        Assert.True(window.ShowInTaskbar);
        Assert.Same(NoticeWindow.NoticeFont, window.Font);
        Assert.Same(TrayIcons.App, window.Icon);
        var area = NoticeWindow.Area(TaskbarWatch.FindTaskbar(), h => Screen.FromHandle(h).WorkingArea, Screen.PrimaryScreen?.WorkingArea);
        Assert.Equal(new Point(area.Right - window.Width - 12, area.Bottom - window.Height - 12), window.Location);
        // Shown, and on screen for its moment: the buttons can be pressed, and Decide later has the focus.
        OnShown(window);
        now += NextWaitTicks;
        window.HoldEnds();
        Assert.Same(window.ButtonFor(UrgentAnswer.Later), window.ActiveControl);
        // Turn on, then its close: one answer.
        window.Press(UrgentAnswer.TurnOn);
        window.Dismissed();
        Assert.Equal(((UrgentAnswer?)UrgentAnswer.TurnOn, 1), (got, answers));
        // Closed with no answer (its close box, Alt+F4, PoolWatch exiting): said once, as no answer.
        using var closed = new UrgentQuestionWindow(5L << 30, Answered);
        closed.Dismissed();
        closed.Dismissed();
        Assert.Equal(((UrgentAnswer?)null, 2), (got, answers));
    }

    /// <summary>Setup's moment (<see cref="SetupSession.NextWait"/>) on the clock the tests move on.</summary>
    private static long NextWaitTicks => (long)(SetupSession.NextWait.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    /// <summary>What WinForms calls once a window is first on screen (<c>Form.OnShown</c>), for a window the tests never show.</summary>
    private static void OnShown(Form form) =>
        typeof(Form).GetMethod("OnShown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, [EventArgs.Empty]);

    /// <summary>
    /// The question opens by itself at a start (often at sign-in), above other windows, in the corner of the screen, so a
    /// click aimed at what was there just before must not answer it: Leave off settles the choice for good, and Turn on
    /// turns an elevated purge on. So its three buttons are greyed, and a press answers nothing, until its window is first
    /// shown and for a second from then (setup's moment, as the Records step that opens alone holds its Done), however
    /// long before that it was made. A timer gives the buttons back once the moment is over; a tick that comes early waits
    /// the rest. A close is not held: it writes nothing.
    /// </summary>
    [Fact]
    public void ItsButtonsAnswerNothingUntilItHasBeenOnScreenForAMoment()
    {
        long now = 0;
        var answers = new List<UrgentAnswer?>();
        using var window = new UrgentQuestionWindow(5L << 30, answers.Add, () => now);
        UrgentAnswer[] all = [UrgentAnswer.TurnOn, UrgentAnswer.LeaveOff, UrgentAnswer.Later];
        void PressAll()
        {
            foreach (var answer in all) window.Press(answer);
        }
        Assert.Equal(TimeSpan.FromSeconds(1), SetupSession.NextWait);
        // Made a minute before it is shown (a slow sign-in): held all that time, greyed, with no timer yet.
        now += 60 * NextWaitTicks;
        Assert.Equal(SetupSession.NextWait, window.HeldFor);
        Assert.All(all, a => Assert.False(window.ButtonFor(a).Enabled, a.ToString()));
        Assert.False(window.HoldRunning);
        PressAll();
        Assert.Empty(answers);
        // Shown: its whole moment from now, and the timer runs.
        OnShown(window);
        Assert.Equal(SetupSession.NextWait, window.HeldFor);
        Assert.True(window.HoldRunning);
        now += NextWaitTicks - 1;
        Assert.True(window.HeldFor > TimeSpan.Zero);
        PressAll();
        Assert.Empty(answers);
        // The timer ticks early: still greyed, and it waits the rest. A window is first shown once: told again, the moment
        // does not start over.
        window.HoldEnds();
        Assert.All(all, a => Assert.False(window.ButtonFor(a).Enabled, a.ToString()));
        Assert.True(window.HoldRunning);
        OnShown(window);
        now += 1;
        Assert.Equal(TimeSpan.Zero, window.HeldFor);
        // The moment is over: the buttons can be pressed, and a press answers, once.
        window.HoldEnds();
        Assert.All(all, a => Assert.True(window.ButtonFor(a).Enabled, a.ToString()));
        Assert.False(window.HoldRunning);
        window.Press(UrgentAnswer.LeaveOff);
        window.Press(UrgentAnswer.TurnOn);
        Assert.Equal((UrgentAnswer?)UrgentAnswer.LeaveOff, Assert.Single(answers));
        // A close inside the moment is given as no answer, which writes nothing (Decide later).
        var closedAnswers = new List<UrgentAnswer?>();
        using var closed = new UrgentQuestionWindow(5L << 30, closedAnswers.Add, () => now);
        OnShown(closed);
        closed.Press(UrgentAnswer.TurnOn);
        closed.Dismissed();
        Assert.Null(Assert.Single(closedAnswers));
    }

    /// <summary>
    /// The tray app asks at its start, once setup has had its turn (setup itself, or the main window), and closes the
    /// question as it exits (the wiring needs the running app, so its lines are held).
    /// </summary>
    [Fact]
    public void TheTrayAppAsksAtItsStartAndClosesTheQuestionAsItExits()
    {
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayApp.cs"));
        Assert.Contains("_urgentQuestion = new UrgentQuestion(live, engine.Log.Info);", tray);
        Assert.Contains("_urgentQuestion.AtStart(Setup.Store.GuideCompleted, smoke);", tray);
        Assert.Contains("_urgentQuestion.Close();", tray);
        Assert.True(tray.IndexOf("_gate.Start(showWindow);", StringComparison.Ordinal) < tray.IndexOf("_urgentQuestion.AtStart(", StringComparison.Ordinal));
    }
}
