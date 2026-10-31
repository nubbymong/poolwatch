using PoolWatch.Core;
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
        "Overhead, steady state over 3 h 12 min: CPU 0.31% of one core (budget 0.5%), peak working set 96.4 MB (budget 100 MB), disk writes " +
        "4.2 MB/h (budget 10 MB/h).\nPoolWatch itself (last 10 min): CPU 0.28% of one core, 91 MB, 3.9 MB/h written - OVER BUDGET (CPU, memory; budget <0.5%, <100 MB, <10 MB/h)",
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsFitsASmallScreenWhole(bool shortcut)
    {
        var offer = shortcut ? new ShortcutOffer(() => Task.FromResult((true, "made"))) : null;
        using var settings = new SettingsDialog(new Settings { CaptureOnAlert = false }, offer);
        var size = Laid(settings);
        Assert.True(size.Width <= Area.Width && size.Height <= Area.Height, $"Settings is {size}, the screen's working area {Area}");
        NothingClipped(settings);
        var texts = All(settings).Select(c => c.Text).ToList();
        // Every part the CI checks, in plain words, and the three rules by name.
        foreach (var item in new[] { "Start at logon", "Start PoolWatch when I sign in", "Automatic cache clearing", "Clear the standby cache automatically",
                     SimpleSettings.ScheduleChoice, SimpleSettings.SizeChoice, SimpleSettings.EitherChoice, "Deeper capture", "Token-leak guard",
                     SimpleSettings.TokenGuardLabel, "Update notices", SimpleSettings.UpdatesLabel, SimpleSettings.UpdatesHint, "Advanced...", "Save", "Close" })
            Assert.Contains(item, texts);
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
}
