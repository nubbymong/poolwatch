using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// Settings, Advanced (<see cref="AdvancedSettingsDialog"/>): the property grid shows each setting with the description
/// the box under it shows for the one selected. Only 12 had one, and the box was empty for the rest, the first the grid
/// opens on included. The grid takes the settings and their descriptions as WinForms' TypeDescriptor gives them, as here.
/// </summary>
public class AdvancedSettingsTests
{
    /// <summary>The properties the grid shows (its default filter: browsable ones), in its own order (by name: the settings have no categories).</summary>
    private static List<PropertyDescriptor> Shown(object o) =>
        TypeDescriptor.GetProperties(o, [BrowsableAttribute.Yes]).Cast<PropertyDescriptor>().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every setting the grid shows has a description, in plain words (no internal names of code types): the first it opens
    /// on, every other, and the two values of a growth alert in the list's own editor. Three are hidden: two that other
    /// controls set (Show RAM on the graph, the service profiles on the Services tab) and the file's format version.
    /// </summary>
    [Fact]
    public void EverySettingTheGridShowsHasADescription()
    {
        var shown = Shown(new Settings());
        Assert.True(shown.Count >= 60, $"the grid shows {shown.Count} settings");
        var missing = shown.Where(p => string.IsNullOrWhiteSpace(p.Description)).Select(p => p.Name).ToList();
        Assert.True(missing.Count == 0, "no description: " + string.Join(", ", missing));
        Assert.False(string.IsNullOrWhiteSpace(shown[0].Description)); // the grid opens on it
        Assert.All(Shown(new GrowthRule()), p => Assert.False(string.IsNullOrWhiteSpace(p.Description), p.Name));
        Assert.Equal(["ServiceProfiles", "SettingsVersion", "ShowRam"],
            TypeDescriptor.GetProperties(typeof(Settings)).Cast<PropertyDescriptor>().Where(p => !p.IsBrowsable).Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var p in shown)
        {
            Assert.True(char.IsUpper(p.Description[0]) || char.IsDigit(p.Description[0]) || p.Description.StartsWith("tags.csv", StringComparison.Ordinal), $"{p.Name}: \"{p.Description}\"");
            Assert.EndsWith(".", p.Description);
            Assert.DoesNotContain("—", p.Description); // the repo's plain style
            // A pool tag's code is said to be one ("the token leak (pool tag Toke)"); "the Toke leak" read as a word.
            if (Regex.IsMatch(p.Description, @"\bToke\b"))
                Assert.True(Regex.IsMatch(p.Description, @"pool tag Toke\b|\bToke pool tag"), $"{p.Name} names Toke without saying it is a pool tag: \"{p.Description}\"");
        }
    }

    /// <summary>How the help box draws its description: wrapped at words, as a label does, no mnemonic.</summary>
    private const TextFormatFlags HelpFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

    /// <summary>
    /// The help box's description under the grid of a dialog made as showing makes it (never shown), at the dialog's own
    /// size. The grid lays its parts out only while it is visible, so the box is given the grid's width here, as the grid
    /// gives it; the box then lays out its own name and description, under the name, as it does on screen.
    /// </summary>
    private static Label HelpBoxDescription(AdvancedSettingsDialog dialog)
    {
        typeof(Control).GetMethod("CreateControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(bool)])!.Invoke(dialog, [true]);
        var grid = dialog.Controls.OfType<PropertyGrid>().Single();
        var help = grid.Controls.Cast<Control>().Single(c => c.GetType().Name is "HelpPane" or "DocComment"); // its name in .NET 10, and before
        help.Width = grid.ClientSize.Width;
        var labels = help.Controls.OfType<Label>().ToList();
        Assert.Equal(2, labels.Count); // the setting's name, then its description
        return labels[1];
    }

    /// <summary>
    /// Every description is whole in the help box at the dialog's own size: the box shows two of its lines under the
    /// setting's name and cuts a third off. Measured as the box draws it, in its font, at its width (658 px at 96 dpi).
    /// Four ran to a third line and lost it (the automatic clear's way, the token-leak guard, the symbol folders and the
    /// version check); a limit of 420 characters let them through.
    /// </summary>
    [Fact]
    public void EveryDescriptionIsWholeInTheHelpBoxAtTheDialogsSize()
    {
        using var f = new ChangeFixture();
        using var dialog = new AdvancedSettingsDialog(new SettingsEditor(analysis: null!, actions: null!, f.Live(), shell: null!));
        var box = HelpBoxDescription(dialog);
        int line = TextRenderer.MeasureText("Ag", box.Font, new Size(int.MaxValue, int.MaxValue), HelpFlags).Height;
        Assert.True(box.ClientSize.Height >= 2 * line && box.ClientSize.Height < 3 * line, $"the help box shows {box.ClientSize.Height} px, lines of {line} px");
        Assert.True(box.ClientSize.Width > 500, $"the help box is {box.ClientSize.Width} px wide");
        int Height(string text) => TextRenderer.MeasureText(text, box.Font, new Size(box.ClientSize.Width, int.MaxValue), HelpFlags).Height;
        var cut = Shown(new Settings()).Where(p => Height(p.Description) > box.ClientSize.Height)
            .Select(p => $"{p.Name}: {Height(p.Description) / line} lines").ToList();
        Assert.True(cut.Count == 0, "cut off in the help box: " + string.Join("; ", cut));
        Assert.True(Height(new string('x', 40) + " " + string.Join(" ", Enumerable.Repeat("word", 60))) > box.ClientSize.Height, "the measure sees nothing cut off");
    }

    /// <summary>
    /// The growth alerts' list editor shows the description of each alert's two values. WinForms' own collection editor
    /// hides the box under its grid that shows it (HelpVisible false), so they showed none. The dialog's grid opens the
    /// list in <see cref="GrowthRulesEditor"/>, whose grid shows that box, and at the editor's own size each description is
    /// whole in its two lines (measured as for the dialog's own grid).
    /// </summary>
    [Fact]
    public void TheGrowthAlertsListEditorShowsEachValuesDescription()
    {
        using var f = new ChangeFixture();
        using (new AdvancedSettingsDialog(new SettingsEditor(analysis: null!, actions: null!, f.Live(), shell: null!))) { }
        var list = TypeDescriptor.GetProperties(new Settings())[nameof(Settings.GrowthRules)]!;
        Assert.IsType<GrowthRulesEditor>(list.GetEditor(typeof(System.Drawing.Design.UITypeEditor)));

        using var form = new GrowthRulesEditor().Form();
        Assert.Equal("Growth alerts", form.Text);
        var grid = Assert.Single(GrowthRulesEditor.GridsIn(form));
        Assert.True(grid.HelpVisible, "the list editor hides the box that shows each value's description");
        typeof(Control).GetMethod("CreateControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(bool)])!.Invoke(form, [true]);
        form.PerformLayout();
        var help = grid.Controls.Cast<Control>().Single(c => c.GetType().Name is "HelpPane" or "DocComment");
        help.Width = grid.ClientSize.Width;
        var box = help.Controls.OfType<Label>().ToList()[1];
        int line = TextRenderer.MeasureText("Ag", box.Font, new Size(int.MaxValue, int.MaxValue), HelpFlags).Height;
        Assert.True(box.ClientSize.Height >= 2 * line, $"the help box shows {box.ClientSize.Height} px, lines of {line} px");
        int Height(string text) => TextRenderer.MeasureText(text, box.Font, new Size(box.ClientSize.Width, int.MaxValue), HelpFlags).Height;
        var values = Shown(new GrowthRule());
        Assert.Equal([nameof(GrowthRule.Bytes), nameof(GrowthRule.WindowMinutes)], values.Select(p => p.Name));
        var cut = values.Where(p => Height(p.Description) > box.ClientSize.Height).Select(p => $"{p.Name}: {Height(p.Description) / line} lines at {box.ClientSize.Width} px").ToList();
        Assert.True(cut.Count == 0, "cut off in the list editor's help box: " + string.Join("; ", cut));
    }

    private const string Number = @"(\d[\d,]*(?:\.\d+)?)";

    private static double Parse(string s) => double.Parse(s.Replace(",", ""), CultureInfo.InvariantCulture);

    private static bool IsNumber(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(double);

    private static object As(Type t, double v) =>
        t == typeof(int) ? (object)(int)Math.Clamp(v, int.MinValue, int.MaxValue) : t == typeof(long) ? (object)(long)Math.Clamp(v, long.MinValue / 2, long.MaxValue / 2) : v;

    private static double Loaded(PropertyDescriptor p, double tried)
    {
        var s = new Settings();
        p.SetValue(s, As(p.PropertyType, tried));
        s.Normalize();
        return Convert.ToDouble(p.GetValue(s), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Each description tells the truth about what the code does with its setting: a switch says whether it is on by
    /// default as it is; a number says its default, which is its default in the code; and where it states a range, "(A to
    /// B", "at most N" or "at least N", loading a value far outside it ends inside it (what Normalize keeps; "0 turns ...
    /// off" where a 0 is kept as off), and the ends themselves are kept. A text or list that states "at most N" (characters,
    /// or apps) or "A local drive path only" is held to it the same way, and the growth alerts' own two values too.
    /// </summary>
    [Fact]
    public void EachDescriptionTellsTheTruthAboutItsDefaultAndItsRange()
    {
        var defaults = new Settings();
        int ranges = 0, limits = 0;
        foreach (var p in Shown(defaults))
        {
            string d = p.Description;
            object? value = p.GetValue(defaults);
            if (value is bool on)
            {
                Assert.True(d.Contains(on ? "On by default" : "Off by default", StringComparison.Ordinal), $"{p.Name} is {(on ? "on" : "off")} by default: \"{d}\"");
                Assert.DoesNotContain(on ? "Off by default" : "On by default", d);
                continue;
            }
            if (!IsNumber(p.PropertyType))
            {
                limits += TextLimitsHold(p, d);
                continue;
            }
            var stated = Regex.Match(d, "default " + Number);
            Assert.True(stated.Success, $"{p.Name}: its description gives no default: \"{d}\"");
            Assert.Equal(Convert.ToDouble(value, CultureInfo.InvariantCulture), Parse(stated.Groups[1].Value));
            bool zeroIsOff = Regex.IsMatch(d, @"\b0 turns [a-z ]+ off");
            var range = Regex.Match(d, @"\(" + Number + " to " + Number);
            if (range.Success)
            {
                ranges++;
                double lo = Parse(range.Groups[1].Value), hi = Parse(range.Groups[2].Value);
                foreach (double tried in new[] { -1e6, lo - 1, hi + 1, 1e9 })
                {
                    double now = Loaded(p, tried);
                    Assert.True((zeroIsOff && now == 0) || (now >= lo && now <= hi), $"{p.Name} loaded as {tried} is {now}, not {lo} to {hi}");
                }
                foreach (double end in new[] { lo, hi }) Assert.Equal(end, Loaded(p, end));
            }
            if (Regex.Match(d, @"\bat least " + Number + @"[;),]") is { Success: true } least)
            {
                ranges++;
                double lo = Parse(least.Groups[1].Value);
                Assert.True(Loaded(p, lo - 1) >= lo && Loaded(p, -1e6) >= lo, $"{p.Name} loads below {lo}");
                Assert.Equal(lo, Loaded(p, lo));
            }
        }
        Assert.True(ranges >= 32, $"only {ranges} ranges were checked");
        Assert.True(limits >= 6, $"only {limits} limits of a text or list were checked");

        // The growth alerts: the list's stated defaults, and each alert's own two values.
        Assert.Contains("Two by default: 200 MB in 10 minutes and 1 GB in 60 minutes.", Describe(nameof(Settings.GrowthRules)));
        Assert.Equal([(10.0, 200L << 20), (60.0, 1L << 30)], defaults.GrowthRules.Select(r => (r.WindowMinutes, r.Bytes)));
        var rule = Shown(new GrowthRule()).ToDictionary(p => p.Name, p => p.Description);
        Assert.Contains("more than 0 and at most 60: a longer window is cut to 60, a rule of 0 is dropped", rule[nameof(GrowthRule.WindowMinutes)]);
        Assert.Contains("more than 0", rule[nameof(GrowthRule.Bytes)]);
        Assert.Contains("a rule of 0 is dropped", rule[nameof(GrowthRule.Bytes)]);
        var rules = new Settings { GrowthRules = [new(120, 5), new(60, 7), new(0, 5), new(10, 0)] };
        rules.Normalize();
        Assert.Equal([(60.0, 5L), (60.0, 7L)], rules.GrowthRules.Select(r => (r.WindowMinutes, r.Bytes)));
    }

    private static string Describe(string name) => Shown(new Settings()).Single(p => p.Name == name).Description;

    /// <summary>
    /// Each text and list setting the grid shows states its default too, and it is the code's (the numbers and switches
    /// are held above): a text empty by default says "Empty (the default)", any other gives its value ("default" and the
    /// value, or the value and "(default)"), and the watched apps give how many there are and two of them. The growth
    /// alerts' own two values give theirs for an alert added in the list's editor, which makes it with them. The watched
    /// apps gave no default, and six more gave none or only implied it: pooltag.txt and the growth alert's two values gave
    /// none, and the three folders only implied theirs. The watched apps also say where their pool tags go, a vendor
    /// bundle (they said the timeline).
    /// </summary>
    [Fact]
    public void EachTextAndListSettingStatesItsDefault()
    {
        var defaults = new Settings();
        var shown = Shown(defaults);
        Assert.All(shown, p => Assert.True(p.PropertyType == typeof(bool) || IsNumber(p.PropertyType) || p.PropertyType == typeof(string) || p.PropertyType.IsEnum
            || p.Name is nameof(Settings.WatchedApps) or nameof(Settings.GrowthRules), $"{p.Name}: a kind of setting this test does not check"));

        // A choice of a few (the grid offers its values in a list) names each value as the grid shows it, and its default
        // as "<value> (default)".
        var choices = shown.Where(p => p.PropertyType.IsEnum).ToList();
        Assert.Equal([nameof(Settings.PoolNotices), nameof(Settings.RecordMode)], choices.Select(p => p.Name)); // RecordMode: what PoolWatch keeps on disk
        foreach (var p in choices)
        {
            Assert.Contains(p.GetValue(defaults) + " (default)", p.Description);
            foreach (string value in Enum.GetNames(p.PropertyType)) Assert.Contains(value, p.Description);
        }

        var texts = shown.Where(p => p.PropertyType == typeof(string)).ToList();
        foreach (var p in texts)
        {
            string d = p.Description, value = (string?)p.GetValue(defaults) ?? "";
            if (value.Length == 0) Assert.True(d.Contains("Empty (the default)", StringComparison.Ordinal), $"{p.Name} is empty by default: \"{d}\"");
            else Assert.True(d.Contains("default " + value, StringComparison.Ordinal) || d.Contains(value + " (default)", StringComparison.Ordinal), $"{p.Name}'s default is {value}: \"{d}\"");
        }
        Assert.Equal(["AiccResourcesRoot", "AutoClearMode", "ClaudeVersionsDir", "MarkHotkey", "PoolTagTxtPath", "SymbolPath"], texts.Select(p => p.Name));

        var watched = Regex.Match(Describe(nameof(Settings.WatchedApps)), @"default (\d+), (\S+) and (\S+) among them");
        Assert.True(watched.Success, "the watched apps give no default: " + Describe(nameof(Settings.WatchedApps)));
        Assert.Equal(defaults.WatchedApps.Count, int.Parse(watched.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Contains(watched.Groups[2].Value, defaults.WatchedApps);
        Assert.Contains(watched.Groups[3].Value, defaults.WatchedApps);

        // Where each watched app's pool tags go: a vendor bundle's tags/ folder (from before it starts and after it exits),
        // not the timeline, which gets its crashes. It said the timeline. Runtimes such as node, which AppSnapshots skips
        // (PileUpAndSnapshotTests holds that node gets no snapshot), are named.
        string apps = Describe(nameof(Settings.WatchedApps));
        Assert.Contains("their crashes go on the timeline; a vendor bundle has the pool tags before and after each runs (not runtimes like node).", apps);
        Assert.Contains("before-launch-<app> / after-exit-<app> for each watched app", Core.Boot.VendorBundle.Parts.Single(p => p.file == "tags/").what);
        Assert.Contains("node.exe", Core.Identity.ProcessIdentities.SharedRuntimes);

        var added = new GrowthRule(); // as the list's editor makes a new alert
        foreach (var p in Shown(added))
        {
            var stated = Regex.Match(p.Description, "default " + Number + " for an alert you add");
            Assert.True(stated.Success, $"GrowthRule.{p.Name} gives no default: \"{p.Description}\"");
            Assert.Equal(Convert.ToDouble(p.GetValue(added), CultureInfo.InvariantCulture), Parse(stated.Groups[1].Value));
        }
    }

    /// <summary>
    /// A text or list setting's stated limits hold: "at most N characters" (one more loads as empty, N is kept), "at most N"
    /// apps (more load as N), "A local drive path only" (a network path loads as something else). The count checked.
    /// </summary>
    private static int TextLimitsHold(PropertyDescriptor p, string d)
    {
        int checkedHere = 0;
        if (p.PropertyType == typeof(string) && Regex.Match(d, @"at most " + Number + " characters") is { Success: true } chars)
        {
            int n = (int)Parse(chars.Groups[1].Value);
            var s = new Settings();
            p.SetValue(s, "C:\\" + new string('a', n + 1 - 3));
            s.Normalize();
            Assert.True(((string?)p.GetValue(s) ?? "").Length <= n, $"{p.Name} keeps a value of {n + 1} characters");
            var kept = new Settings();
            string fits = "Ctrl+" + new string('a', n - 5);
            p.SetValue(kept, fits);
            kept.Normalize();
            Assert.Equal(fits, (string?)p.GetValue(kept));
            checkedHere++;
        }
        if (p.PropertyType == typeof(List<string>) && Regex.Match(d, @"\(at most " + Number) is { Success: true } count)
        {
            int n = (int)Parse(count.Groups[1].Value);
            var s = new Settings();
            p.SetValue(s, Enumerable.Range(0, n + 20).Select(i => "app" + i).ToList());
            s.Normalize();
            Assert.Equal(n, ((List<string>)p.GetValue(s)!).Count);
            var each = Regex.Match(d, @"each up to " + Number + " characters");
            Assert.True(each.Success, $"{p.Name} gives no length for each name: \"{d}\"");
            int longest = (int)Parse(each.Groups[1].Value);
            var names = new Settings();
            p.SetValue(names, new List<string> { new('a', longest), new('b', longest + 1) });
            names.Normalize();
            Assert.Equal([new string('a', longest)], (List<string>)p.GetValue(names)!);
            checkedHere++;
        }
        if (p.PropertyType == typeof(string) && d.Contains("A local drive path only", StringComparison.Ordinal))
        {
            var s = new Settings();
            p.SetValue(s, @"\\server\share\folder");
            s.Normalize();
            Assert.NotEqual(@"\\server\share\folder", (string?)p.GetValue(s));
            var local = new Settings();
            p.SetValue(local, @"D:\tools\folder");
            local.Normalize();
            Assert.Equal(@"D:\tools\folder", (string?)p.GetValue(local));
            checkedHere++;
        }
        return checkedHere;
    }
}
