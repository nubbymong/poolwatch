using PoolWatch.Core;
using PoolWatch.Core.Setup;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// About and Settings, as they lay out with their longest texts: each fits a 1024 x 600 screen whole (About was 857 px
/// tall and scrolled on a 1080p laptop at 125%), and nothing in it is cut off or outside its parent. The windows get a
/// native window here but are never shown. Every size in them is logical pixels scaled with the display, so what fits at
/// 100% fits at 125%, 150% and 200% on a screen as many logical pixels large (ScreenFitTests holds the scrolling fallback
/// for a screen shorter still).
/// </summary>
[Collection(nameof(WholeWindowLayout))]
public class WindowFitTests
{
    /// <summary>The smallest screen PoolWatch's windows fit whole, in logical pixels, less a 48-pixel taskbar.</summary>
    private static readonly Size Area = new(1024, 600 - 48);

    private static AboutFacts LongAbout() => new("1.0.0",
        "1,234,567 samples; process events (ETW) kernel session (shared); pooltag.txt not found (built-in tag table and driver scan).",
        "Overhead over 3 h 12 min of steady state (from 15 minutes after start): CPU 0.31% of one core (budget 0.5%), peak working set 96.4 MB (budget 100 MB), disk writes " +
        "4.2 MB/h (budget 10 MB/h).\nPoolWatch itself (last 10 min): CPU 0.28% of one core, 91 MB now, 3.9 MB/h written - OVER BUDGET (CPU, memory; budget <0.5%, <100 MB, <10 MB/h)",
        @"C:\ProgramData\PoolWatch");

    /// <summary>
    /// A control's own Visible flag, not its window's: these windows are never shown, so <see cref="Control.Visible"/> is
    /// false for every control in them and the layout check would look at none.
    /// </summary>
    internal static bool OwnVisible(Control c) => (bool)GetState.Invoke(c, [VisibleState])!;

    /// <summary>WinForms' own Control.GetState(States): found by name, so a WinForms that renames either fails here, by name.</summary>
    private static readonly System.Reflection.MethodInfo GetState = typeof(Control)
        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Single(m => m.Name == "GetState" && m.GetParameters() is [{ ParameterType.IsEnum: true }]);

    /// <summary>The States member for a control's own Visible flag, by its name (Enum.Parse throws when there is none).</summary>
    private static readonly object VisibleState = Enum.Parse(GetState.GetParameters()[0].ParameterType, "Visible");

    private static List<string> Clipped(Form form)
    {
        var problems = new List<string>();
        MainForm.ClippedControls(form, problems, OwnVisible);
        return problems;
    }

    /// <summary>Nothing in <paramref name="form"/> is cut off; else every problem in full, with the window's size, font and dpi.</summary>
    private static void NothingClipped(Form form)
    {
        var problems = Clipped(form);
        Assert.True(problems.Count == 0, $"{form.Text} is {form.Size} (client {form.ClientSize}, font {form.Font.Height} px, {form.DeviceDpi} dpi):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheLayoutCheckLooksAtTheControlsOfAWindowNeverShown()
    {
        using var form = new Form { ClientSize = new Size(200, 100) };
        var panel = new Panel { Bounds = new Rectangle(0, 0, 200, 100) };
        var box = new CheckBox { Text = "A label far too long for the panel it sits in, so it runs past its edge", AutoSize = true };
        var hidden = new Button { Bounds = new Rectangle(300, 0, 50, 20), Visible = false };
        panel.Controls.Add(box);
        panel.Controls.Add(hidden);
        form.Controls.Add(panel);
        Laid(form);
        Assert.False(box.Visible); // the window is not shown
        Assert.Contains(Clipped(form), p => p.StartsWith("CheckBox", StringComparison.Ordinal) && p.Contains("is outside", StringComparison.Ordinal));
        Assert.DoesNotContain(Clipped(form), p => p.StartsWith("Button", StringComparison.Ordinal)); // hidden: not looked at
        box.MaximumSize = new Size(200, 0); // it wraps
        form.PerformLayout();
        Assert.Empty(Clipped(form));
    }

    /// <summary>
    /// The layout check (<see cref="MainForm.ClippedControls"/>, which the CI UI test reports for every window, and
    /// setup's own) sees shown controls that lie on their siblings, as a green line docked at the bottom once lay over the
    /// title above it with every control still inside its parent and whole: two pixels each way is an overlap. Not two that
    /// only touch or share one pixel's row, not a hidden one, and not a tab control's pages, which share its area and show
    /// one at a time (held with every page counted as shown, as once the window has a handle only the selected one is).
    /// </summary>
    [Fact]
    public void TheLayoutCheckSeesControlsThatOverlapTheirSiblings()
    {
        using var form = new Form { ClientSize = new Size(300, 300) };
        var panel = new Panel { Bounds = new Rectangle(0, 0, 300, 300) };
        var title = new Label { Text = "Title", AutoSize = false, Bounds = new Rectangle(0, 0, 200, 30) };
        var line = new Label { Text = "Handled", AutoSize = false, Bounds = new Rectangle(0, 20, 200, 30) }; // over the title's last 10 px
        var touching = new Label { Text = "Touching", AutoSize = false, Bounds = new Rectangle(0, 50, 200, 30) };
        var onePixel = new Label { Text = "One pixel", AutoSize = false, Bounds = new Rectangle(0, 79, 200, 30) };
        var hidden = new Label { Text = "Hidden", AutoSize = false, Bounds = new Rectangle(0, 0, 200, 30), Visible = false };
        var tabs = new TabControl { Bounds = new Rectangle(0, 150, 300, 150) };
        tabs.TabPages.Add(new TabPage("One"));
        tabs.TabPages.Add(new TabPage("Two"));
        panel.Controls.AddRange([title, line, touching, onePixel, hidden, tabs]);
        form.Controls.Add(panel);
        Laid(form);
        var overlaps = Clipped(form).Where(p => p.Contains(" overlap ", StringComparison.Ordinal)).ToList();
        Assert.True(overlaps.Count == 1, string.Join(Environment.NewLine, Clipped(form)));
        Assert.StartsWith("Label \"Title\" at {X=0,Y=0,Width=200,Height=30} and Label \"Handled\" at {X=0,Y=20,Width=200,Height=30} overlap by {Width=200, Height=10}", overlaps[0]);
        line.Bounds = new Rectangle(0, 107, 200, 30); // two pixels over the one-pixel label's foot
        Assert.Equal(["Label \"Handled\" at {X=0,Y=107,Width=200,Height=30} and Label \"One pixel\" at {X=0,Y=79,Width=200,Height=30} overlap by {Width=200, Height=2}"],
            Clipped(form).Where(p => p.Contains(" overlap ", StringComparison.Ordinal)).Select(p => p[..p.IndexOf(" in ", StringComparison.Ordinal)]));
        line.Bounds = new Rectangle(198, 0, 50, 30); // two pixels over the title's right edge
        Assert.Equal(["Label \"Title\" at {X=0,Y=0,Width=200,Height=30} and Label \"Handled\" at {X=198,Y=0,Width=50,Height=30} overlap by {Width=2, Height=30}"],
            Clipped(form).Where(p => p.Contains(" overlap ", StringComparison.Ordinal)).Select(p => p[..p.IndexOf(" in ", StringComparison.Ordinal)]));
        line.Bounds = new Rectangle(0, 110, 200, 30); // under the others now
        Assert.Empty(Clipped(form));
        // The pages, all counted as shown (as before the tab control has a handle, when none is hidden yet): not compared.
        var pages = new List<string>();
        MainForm.OverlappingSiblings(tabs, pages, _ => true);
        Assert.Empty(pages);
        Assert.True(Rectangle.Intersect(tabs.TabPages[0].Bounds, tabs.TabPages[1].Bounds) is { Width: > 1, Height: > 1 }, "the pages do not share an area"); // they do

        // Setup's check, on controls with no window around them (as its views are tested, and visible so).
        var page = new Panel { Size = new Size(300, 100) };
        page.Controls.AddRange([new Label { Text = "Step", AutoSize = false, Bounds = new Rectangle(0, 0, 200, 30) }, new Label { Text = "Words", AutoSize = false, Bounds = new Rectangle(100, 10, 200, 30) }]);
        var problems = new List<string>();
        PoolWatch.Ui.Setup.SetupWizardView.Check(page, problems);
        Assert.Equal(["Label \"Step\" at {X=0,Y=0,Width=200,Height=30} and Label \"Words\" at {X=100,Y=10,Width=200,Height=30} overlap by {Width=100, Height=20} in Panel \"\""], problems);
    }

    private static Size Laid(Form form)
    {
        _ = form.Handle;     // the native window, never shown
        form.PerformLayout();
        return form.Size;
    }

    [Fact]
    public void AboutFitsASmallScreenWholeWithEveryCredit()
    {
        using var about = new AboutDialog(LongAbout(), actions: null);
        var size = Laid(about);
        Assert.True(size.Width <= Area.Width && size.Height <= Area.Height, $"About is {size}, the screen's working area {Area}");
        NothingClipped(about);
        // Nothing was folded away: every credit, the data folder and both paragraphs about changes and the network.
        using var stats = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(about.SmokeStats()));
        Assert.Equal(Credits.All.Count, stats.RootElement.GetProperty("credits").GetArrayLength());
        var texts = All(about).Select(c => c.Text).ToList();
        Assert.Contains(@"C:\ProgramData\PoolWatch", texts);
        Assert.Contains(AboutDialog.Network, texts);
        Assert.Contains(texts, t => t.StartsWith("PoolWatch watches and reads.", StringComparison.Ordinal) && t.Contains("Active changes", StringComparison.Ordinal)
                                    && t.Contains("a desktop shortcut for Codex", StringComparison.Ordinal)); // as setup and Settings name it
        Assert.False(about.AutoScroll); // it does not scroll on a screen this size
    }

    /// <summary>
    /// Every line Settings can show under its sign-in box (<see cref="SettingsDialog.LogonTick"/>), with a Windows error
    /// message as long as Task Scheduler's usual ones, or none (-1), each with and without the Codex offer.
    /// </summary>
    public static IEnumerable<object[]> LogonNotes()
    {
        int count = LogonNoteTexts().Count;
        foreach (bool shortcut in new[] { true, false })
            for (int note = -1; note < count; note++) yield return [shortcut, note];
    }

    private static List<string> LogonNoteTexts() =>
        Enum.GetValues<SignInState>().Select(s => SettingsDialog.LogonTick(s, null).Note)
            .Append(SettingsDialog.LogonTick(SignInState.NotKnown, "Access is denied. (Exception from HRESULT: 0x80070005 (E_ACCESSDENIED))").Note)
            .OfType<string>().Distinct().ToList();

    [Theory]
    [MemberData(nameof(LogonNotes))]
    public void SettingsFitsASmallScreenWhole(bool shortcut, int logonNote)
    {
        var offer = shortcut ? new ShortcutOffer(() => Task.FromResult((true, "made"))) : null;
        string? note = logonNote >= 0 ? LogonNoteTexts()[logonNote] : null;
        using var settings = new SettingsDialog(new Settings { CaptureOnAlert = false }, offer, note);
        var size = Laid(settings);
        Assert.True(size.Width <= Area.Width && size.Height <= Area.Height, $"Settings is {size}, the screen's working area {Area}");
        NothingClipped(settings);
        var texts = All(settings).Select(c => c.Text).ToList();
        // The line under the sign-in box, when there is one, is shown whole and wraps within its column, on two lines.
        if (note is not null)
        {
            var line = All(settings).Single(c => c.Text == note);
            Assert.True(OwnVisible(line) && line.Right <= line.Parent!.ClientSize.Width, $"{line.Bounds} in {line.Parent!.ClientSize}");
            Assert.InRange(line.Height, 2 * line.Font.Height, 3 * line.Font.Height - 1);
        }
        else Assert.DoesNotContain(All(settings), c => OwnVisible(c) && LogonNoteTexts().Contains(c.Text));
        // Every part the CI checks, in plain words, and the three rules by name.
        foreach (var item in new[] { "Start at logon", "Start PoolWatch when I sign in", "Automatic cache clearing", "Clear the standby cache automatically",
                     SimpleSettings.ScheduleChoice, SimpleSettings.SizeChoice, SimpleSettings.EitherChoice, "Deeper capture", "Token-leak guard",
                     SimpleSettings.TokenGuardLabel, "Update notices", SimpleSettings.UpdatesLabel, SimpleSettings.UpdatesHint, "Advanced...", "Save", "Close",
                     "Notifications", SimpleSettings.EveryGrowthChoice, SimpleSettings.NeedsAttentionChoice, SimpleSettings.NoticesOffChoice, SimpleSettings.NoticesHint })
            Assert.Contains(item, texts);
        // The notifications' three choices show whole, one under another, within their column.
        var choices = settings.Fields.NoticeChoices;
        Assert.All(choices, c => Assert.True(c.Right <= c.Parent!.ClientSize.Width && c.Parent.Right <= c.Parent.Parent!.ClientSize.Width, $"{c.Text}: {c.Bounds} in {c.Parent!.Bounds}"));
        Assert.True(choices[0].Bottom <= choices[1].Top && choices[1].Bottom <= choices[2].Top, string.Join(", ", choices.Select(c => c.Bounds)));
        Assert.Contains(texts, t => t.StartsWith(SimpleSettings.ClearStutter, StringComparison.Ordinal)); // said where clearing is turned on
        Assert.Equal(shortcut, texts.Contains(ShortcutOffer.Button));
        Assert.Equal(shortcut, texts.Contains(ShortcutOffer.Text));
        // The guard's long label wraps within its column, on two lines.
        var guard = settings.Fields.TokenGuard;
        Assert.True(guard.Right <= guard.Parent!.ClientSize.Width, $"{guard.Bounds} in {guard.Parent.ClientSize}");
        Assert.True(guard.Height >= 2 * guard.Font.Height, $"{guard.Size}");
    }

    [Fact]
    public void AWrappingCheckBoxGrowsTallerOnlyWhenItsLabelIsWiderThanItMayBe()
    {
        using var box = new WrapCheckBox { Text = SimpleSettings.TokenGuardLabel, AutoSize = true, Font = new Font("Segoe UI", 9f) };
        var one = box.GetPreferredSize(Size.Empty);
        box.WrapWidth = one.Width + 10;
        Assert.Equal(one, box.GetPreferredSize(Size.Empty)); // it fits: one line, as before
        box.WrapWidth = one.Width * 2 / 3;
        var two = box.GetPreferredSize(Size.Empty);
        Assert.Equal(box.WrapWidth, two.Width);
        Assert.InRange(two.Height, one.Height + box.Font.Height - 2, one.Height + box.Font.Height + 2); // one more line
        box.WrapWidth = 0;
        Assert.Equal(one, box.GetPreferredSize(Size.Empty));
    }

    [Fact]
    public void SettingsOffersTheCodexShortcutOnlyWhenItCanBeMadeAgain()
    {
        Assert.True(ShortcutOffer.Offered(chatGptInstalled: true, launcherInstalled: true, onDesktop: false));
        Assert.False(ShortcutOffer.Offered(chatGptInstalled: true, launcherInstalled: true, onDesktop: true));   // it is there
        Assert.False(ShortcutOffer.Offered(chatGptInstalled: false, launcherInstalled: true, onDesktop: false)); // no app to start
        Assert.False(ShortcutOffer.Offered(chatGptInstalled: true, launcherInstalled: false, onDesktop: false)); // a zip install
        Assert.Contains("PoolWatch doesn't change the Codex app", ShortcutOffer.Text);

        // Whether it is on the desktop, as the window asks before it opens Settings: a desktop it cannot read offers nothing.
        const string desktop = @"C:\Users\Example\Desktop";
        string shortcut = Path.Combine(desktop, Engine.ChatGptShortcut.FileName);
        Assert.True(ShortcutOffer.OnDesktop(() => desktop, p => p == shortcut));
        Assert.False(ShortcutOffer.OnDesktop(() => desktop, _ => false));
        Assert.True(ShortcutOffer.OnDesktop(() => "", _ => false));
        Assert.True(ShortcutOffer.OnDesktop(() => throw new PlatformNotSupportedException(), _ => false));
        Assert.True(ShortcutOffer.OnDesktop(() => desktop, _ => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void TheClearFieldsFollowTheRulePicked()
    {
        using var settings = new SettingsDialog(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1, AutoClearPoolGb = 0 }, null);
        Laid(settings);
        var f = settings.Fields;
        Assert.True(f.Schedule.Checked);
        Assert.True(f.Hours.Parent!.Enabled);
        Assert.False(f.PoolGb.Parent!.Enabled); // the size is not used by a schedule alone
        f.Either.Checked = true;
        Assert.True(f.PoolGb.Parent!.Enabled && f.Hours.Parent!.Enabled);
        f.PoolSize.Checked = true;
        Assert.False(f.Hours.Parent!.Enabled);
        f.AutoClear.Checked = false;
        Assert.False(f.PoolGb.Parent!.Enabled || f.Either.Parent!.Enabled);
    }

    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));

    /// <summary>
    /// About's credits start with the people whose findings PoolWatch's fixes build on (the token leak's two, then the
    /// Codex shader cache's), and those three show whole in the list as About opens, without scrolling, at the longest
    /// texts and with the shortest overhead text (a PoolWatch still in its start-up). The Codex credit was tenth, below
    /// the list's fold.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AboutShowsTheFindersCreditsWithoutScrolling(bool longTexts)
    {
        var facts = longTexts ? LongAbout() : LongAbout() with
        {
            Overhead = AboutDialog.OverheadText(new OverheadReport { WallSeconds = 60, JudgedFromSeconds = 900 }, () => ""),
        };
        using var about = new AboutDialog(facts, actions: null);
        Laid(about);
        var (list, links) = about.CreditsShown;
        Assert.Equal(["bentoner/windows-token-leak", "gunrgbcr/windows-token-leak-logon-fix", "Faarfarin, openai/codex issue 31212"],
            Credits.All.Take(3).Select(c => c.Name));
        Assert.Equal(0, list.AutoScrollPosition.Y);
        foreach (var link in Credits.All.Take(3).Select(c => links.Single(l => l.Text.StartsWith(c.Name + " (", StringComparison.Ordinal))))
        {
            var inList = new Rectangle(link.Left + link.Parent!.Left, link.Top + link.Parent.Top, link.Width, link.Height); // the links sit on one table in the list
            Assert.True(inList.Top >= 0 && inList.Bottom <= list.ClientSize.Height,
                $"\"{link.Text[..link.LinkArea.Length]}\" at {inList} is out of view in the credits' list ({list.ClientSize})");
        }
        Assert.True(list.DisplayRectangle.Height > list.ClientSize.Height, "the list no longer scrolls: the test sees nothing below the fold");
    }


    // ---------------------------------------------------------------- PoolWatch's own cost, as About judges it

    private const long MB = 1L << 20;

    /// <summary>
    /// A run as in the CI UI test, then a steady hour: 70 MB at the warm-up, 138 MB at 90 s (the test has just built,
    /// drawn and saved every window), 120 MB at 5 minutes (after the 30 s warm-up, still the start-up), 12% of one core
    /// and fast writes until 15 minutes; then 70 MB with one reading of 84 MB at 40 minutes, 0.31% of one core and
    /// 1.2 MB of writes an hour.
    /// </summary>
    private static long WorkingSetAt(int s) => s switch { 90 => 138 * MB, 300 => 120 * MB, 2400 => 84 * MB, _ => 70 * MB };
    private static TimeSpan CpuAt(int s) => TimeSpan.FromSeconds(s < 900 ? 0.12 * s : 108 + 0.0031 * (s - 900));
    private static ulong WritesAt(int s) => s < 900 ? (ulong)s * 20_000 : 18_000_000 + (ulong)((s - 900) * 1.2 * MB / 3600);

    /// <summary>Readings every 30 s from <paramref name="from"/> to <paramref name="to"/> seconds, as the sampler takes them.</summary>
    private static void Run(SelfMeter meter, int from, int to)
    {
        for (int s = from; s <= to; s += 30)
        {
            int at = s;
            meter.Tick(CpuAt(at), (WorkingSetAt(at), WorkingSetAt(at)), TimeSpan.FromSeconds(at), () => WritesAt(at));
        }
    }

    private static OverheadReport ReportAt(SelfMeter meter, int s) => meter.Report(TimeSpan.FromSeconds(s), CpuAt(s), WorkingSetAt(s), WorkingSetAt(s), WritesAt(s));

    private const string Budget = "against its budget: under 0.5% of one core, 100 MB of working set and 5 MB of disk writes an hour.";

    /// <summary>
    /// About judges PoolWatch's own cost over its steady state only, which starts 15 minutes after start, as the soak
    /// judges it (DESIGN.md sections 3 and 12), and only once that has lasted 5 minutes. Judged from the 30 s warm-up, it
    /// held the rest of the start-up against the budget: the CI screenshot, about 90 s in, showed CPU 12.38% and 138 MB
    /// (the working set right after the test built and drew every window), and a reading of 138 MB at 90 s stayed the
    /// "steady" peak for good. Here the 90 s and 5-minute readings never count, the whole-run peak still holds them (the
    /// CI overhead check reads it), and the hour after 15 minutes is judged on its own figures.
    /// </summary>
    [Fact]
    public void AboutJudgesTheOverheadOverTheSteadyStateFrom15Minutes()
    {
        var meter = new SelfMeter(TimeSpan.FromSeconds(30));
        Run(meter, 0, 90);
        var screenshot = ReportAt(meter, 90);
        Assert.False(screenshot.Judged);
        Assert.Equal((0.0, 0L, 0L), (screenshot.JudgedSeconds, screenshot.JudgedPeakWorkingSetBytes, screenshot.JudgedDiskWriteBytesPerHour));
        Assert.Equal(138 * MB, screenshot.PeakWorkingSetBytes);
        Assert.Equal("Overhead: judged over its steady state, from 15 minutes after PoolWatch starts, once that has lasted 5 minutes " +
                     "(PoolWatch has run 1 min), " + Budget, AboutDialog.OverheadText(screenshot, () => throw new InvalidOperationException("not read in the start-up")));

        Run(meter, 120, 19 * 60); // past the 30 s warm-up and the start-up trims; steady state not 5 minutes old yet
        var early = ReportAt(meter, 19 * 60);
        Assert.True(early.Judged);
        Assert.Equal(240, early.JudgedSeconds);
        Assert.Equal(70 * MB, early.JudgedPeakWorkingSetBytes); // not the 138 MB at 90 s nor the 120 MB at 5 minutes
        Assert.StartsWith("Overhead: judged over its steady state, from 15 minutes after PoolWatch starts, once that has lasted 5 minutes (PoolWatch has run 19 min)",
            AboutDialog.OverheadText(early, () => ""));

        Run(meter, 19 * 60 + 30, 75 * 60);
        var hour = ReportAt(meter, 75 * 60);
        Assert.Equal(3600, hour.JudgedSeconds);
        Assert.Equal(84 * MB, hour.JudgedPeakWorkingSetBytes);
        Assert.Equal(138 * MB, hour.PeakWorkingSetBytes);
        Assert.Equal("Overhead over 1 h 0 min of steady state (from 15 minutes after start): CPU 0.31% of one core (budget 0.5%), peak working set 84 MB " +
                     "(budget 100 MB), disk writes 1.2 MB/h (budget 5 MB/h).\nthe rolling line", AboutDialog.OverheadText(hour, () => "the rolling line"));
        Assert.True(hour.CpuPercentOfOneCore > 1, "the figures from the warm-up still hold the start-up's CPU (overhead.json, the CI overhead check)");
    }

    /// <summary>
    /// Steady state starts at the warm-up where that is later than 15 minutes, and never earlier than 15 minutes. With a
    /// warm-up over 5 minutes (a sample interval over 100 s) About took the judged branch at 5 minutes of wall time, with
    /// no steady figure yet: "peak working set 0 B".
    /// </summary>
    [Theory]
    [InlineData(360, 900)]  // a 6-minute warm-up (a 120 s sample interval)
    [InlineData(30, 900)]   // the default
    [InlineData(990, 990)]  // a warm-up longer than 15 minutes (the meter's own rule; the settings stop at 15)
    public void SteadyStateStartsAt15MinutesOrAtALongerWarmUp(int warmup, int steadyFrom)
    {
        var meter = new SelfMeter(TimeSpan.FromSeconds(warmup));
        Assert.Equal(TimeSpan.FromSeconds(steadyFrom), meter.SteadyFrom);
        int lastBefore = steadyFrom + 270;
        Run(meter, 0, lastBefore);
        foreach (int s in new[] { 330, Math.Min(warmup + 330, lastBefore), lastBefore }) // 5.5 minutes of wall time, and past the warm-up
        {
            var r = ReportAt(meter, s);
            string text = AboutDialog.OverheadText(r, () => throw new InvalidOperationException("judged before 5 minutes of steady state"));
            Assert.StartsWith($"Overhead: judged over its steady state, from {SelfMeter.SteadyFromWords(TimeSpan.FromSeconds(steadyFrom))} after PoolWatch starts", text);
            Assert.DoesNotContain(" 0 B", text);
        }
        Run(meter, lastBefore + 30, steadyFrom + 300);
        var judged = ReportAt(meter, steadyFrom + 300);
        Assert.Equal(300, judged.JudgedSeconds);
        Assert.StartsWith($"Overhead over 5 min of steady state (from {SelfMeter.SteadyFromWords(TimeSpan.FromSeconds(steadyFrom))} after start): CPU ",
            AboutDialog.OverheadText(judged, () => ""));
        Assert.Equal(["15 minutes", "16 minutes 30 seconds"], new[] { 900, 990 }.Select(x => SelfMeter.SteadyFromWords(TimeSpan.FromSeconds(x))));
        Assert.Equal(["30-second", "1-minute", "2-minute 30-second"], new[] { 30, 60, 150 }.Select(x => SelfMeter.StartupWords(TimeSpan.FromSeconds(x))));
    }

    /// <summary>
    /// The rolling line (in every report, and in About after its figures) gives its figures from the warm-up, but warns
    /// "OVER BUDGET" only over a window that is all steady state: one that holds part of the first 15 minutes says it is
    /// not judged. It warned over the start-up too, so a report written ten minutes in could call PoolWatch over budget
    /// for its own start-up. Its working set is the one now, and it says so ("80 MB now"): "80 MB" read like a figure over
    /// the window, as the CPU and the writes are.
    /// </summary>
    [Fact]
    public void TheRollingLineWarnsOnlyOverAWindowOfSteadyState()
    {
        var meter = new SelfMeter(TimeSpan.FromSeconds(30));
        static TimeSpan Busy(int s) => TimeSpan.FromSeconds(0.02 * s); // 2% of one core throughout
        for (int s = 0; s <= 1500; s += 30)
        {
            meter.Tick(Busy(s), (80 * MB, 60 * MB), TimeSpan.FromSeconds(s), () => 0);
            if (s == 600)
                Assert.Equal("PoolWatch itself (last 9 min): CPU 2.00% of one core, 80 MB now, 0.0 MB/h written (not judged against the budget: PoolWatch's " +
                             "first 15 minutes are start-up)", meter.OwnCostLine(TimeSpan.FromSeconds(s), Busy(s), 80 * MB, 0));
            if (s == 1440) Assert.Contains("not judged", meter.OwnCostLine(TimeSpan.FromSeconds(s), Busy(s), 80 * MB, 0)); // its window starts at 14 min
        }
        Assert.Equal("PoolWatch itself (last 10 min): CPU 2.00% of one core, 80 MB now, 0.0 MB/h written - OVER BUDGET (CPU; budget <0.5%, <100 MB, <5 MB/h)",
            meter.OwnCostLine(TimeSpan.FromSeconds(1500), Busy(1500), 80 * MB, 0));
        Assert.StartsWith("PoolWatch itself: 80 MB now; CPU and writes measured after its 30-second start-up",
            new SelfMeter(TimeSpan.FromSeconds(30)).OwnCostLine(TimeSpan.FromSeconds(10), TimeSpan.Zero, 80 * MB, 0));
    }
}
