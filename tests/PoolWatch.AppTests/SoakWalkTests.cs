using System.Text.RegularExpressions;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The CI UI soak's walk through the main window (<c>SoakWalk</c>, <c>MainForm.StartSoakCycle</c>) and what
/// <c>ci/soak.ps1</c> expects of it: the Simple view and every tab of the Advanced view, one a minute, each refresh
/// profiled for the view on screen, and nothing more once it is stopped for the end-of-run screenshots; and what the
/// About drawn first in that end-of-run walk says, held to the budget.
/// </summary>
public class SoakWalkTests
{
    /// <summary>The Advanced view's tabs, as <c>ci/m4-ui.ps1</c> checks them on the window the CI UI test opens.</summary>
    private static readonly string[] Tabs = ["Pool causes", "Services", "Tasks and startup apps"];

    /// <summary>The soak's two page assertions (their conditions) and the CHECK-RAN marker of each.</summary>
    private static readonly (string Condition, string Id)[] PageChecks =
    [
        ("@($walked | Where-Object { $_ -notin $uiPages }).Count -eq 0", "soak-walked-pages-refreshed"),
        ("@($uiPages | Where-Object { $_ -notin $walked }).Count -eq 0", "soak-no-other-page-refreshed"),
    ];

    private static string CiFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return Path.Combine(dir.FullName, "ci");
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>That a pin fails on a copy of a script: its read of the copy fails, or the read holds no such check.</summary>
    private static void PinFails(Func<string?> why) => Assert.NotNull(ScriptCodeTests.WhyThePinFails(() => why() is null));

    [Fact]
    public void TheWalkShowsTheSimpleViewThenEachTabInTurnAndNothingOnceStopped()
    {
        var walk = new SoakWalk(Tabs.Length);
        var pages = Enumerable.Range(0, 2 * (Tabs.Length + 1)).Select(_ => walk.Next()).ToList();
        Assert.Equal([-1, 0, 1, 2, -1, 0, 1, 2], pages);
        walk.Stop(); // the end-of-run screenshots: the step due with them must not change the view
        Assert.Null(walk.Next());
        Assert.Null(walk.Next());
        Assert.True(walk.Stopped);
    }

    [Fact]
    public void TheSoakExpectsEveryPageTheWalkShowsAndNoOther()
    {
        string ci = CiFolder();
        // The UI test checks that the window has exactly these tabs, so the walk shows the Simple view and these three.
        Assert.Null(WhyTheTabsAreNotChecked(File.ReadAllText(Path.Combine(ci, "m4-ui.ps1"))));
        var stages = SoakWalk.Stages(Tabs.Length);
        Assert.Equal(["ui.simple", "ui.page0", "ui.page1", "ui.page2"], stages);

        // ci/soak.ps1 names them in one list, and its two page assertions compare that list with the pages refreshed.
        Assert.Null(WhyThePagesAreNotChecked(File.ReadAllText(Path.Combine(ci, "soak.ps1")), stages));
    }

    /// <summary>
    /// Why <c>ci/soak.ps1</c> (<paramref name="soak"/>) does not check the pages <paramref name="stages"/> name, or null when
    /// it does, read through <see cref="ScriptCode.StatementsIn"/> (the script's code, comments out). One statement of the
    /// script's top level sets <c>$walked</c> to the stages, as strings and nothing else (<see cref="ScriptCode.StringList"/>:
    /// not an index or a pipe after them), and it is the only assignment of <c>$walked</c> in the script's code
    /// (<see cref="ScriptCode.PowerShellAssignments"/>); and each page assertion is an Assert-True of its own with that
    /// condition in the -Ui part (<c>if ($Ui) { }</c>) of the -Assert block (<c>if ($Assert) { }</c>), itself a statement of
    /// the script, with its CHECK-RAN marker as the next statement. So an assertion edited, removed, commented out, in a
    /// string, or moved into another block (<c>if ($false) { }</c> around it, say), or <c>$walked</c> set again by another
    /// assignment, fails this pin; an assertion that the script no longer reaches as it runs fails a run of the soak's CI job
    /// instead (its ui leg), as it prints no marker (<see cref="CheckMarkerTests"/>). Neither reads <c>$walked</c> set
    /// another way (Set-Variable, say), nor where <c>$uiPages</c>, the pages the soak read from PoolWatch's profile, is set:
    /// that is the data the checks read.
    /// </summary>
    internal static string? WhyThePagesAreNotChecked(string soak, IReadOnlyList<string> stages)
    {
        int sets = ScriptCode.PowerShellAssignments(soak, "walked");
        if (sets != 1) return $"ci/soak.ps1's code assigns $walked {sets} times, not once (its list's own statement)";
        var walked = ScriptCode.StatementsIn(soak, "$walked = @(");
        if (walked.Count != 1) return $"ci/soak.ps1 has {walked.Count} statements of its top level that set $walked, not one";
        var list = Regex.Match(walked[0], @"^\$walked = @\((.*)\)$");
        if (!list.Success || ScriptCode.StringList(list.Groups[1].Value) is not { } named || !named.SequenceEqual(stages))
            return $"$walked is not set to {string.Join(", ", stages)} as strings and nothing else: {walked[0]}";
        const string asserts = "if ($Assert) {", ui = "if ($Ui) {";
        if (ScriptCode.StatementsOf(soak, asserts, ui) is null) return "ci/soak.ps1 has no -Ui part of its own in its -Assert block";
        foreach (var (condition, id) in PageChecks)
            if (!ScriptCode.CheckThenMarker(soak, $"Assert-True ({condition}) \"", id, asserts, ui))
                return $"ci/soak.ps1 does not assert {condition} once, as a statement of its own in its -Ui checks with its marker next";
        return null;
    }

    /// <summary>
    /// Why <c>ci/m4-ui.ps1</c> (<paramref name="ui"/>) does not check that the window has exactly the tabs
    /// <see cref="Tabs"/> names, in order, or null when it does. Read as <see cref="WhyThePagesAreNotChecked"/> reads the
    /// soak: the check must be an Assert-True of its own, with exactly that condition, in the block that reads smoke.json
    /// (<c>if (Test-Path $smokeFile) { }</c>), itself a statement of the script, with its CHECK-RAN marker next.
    /// </summary>
    internal static string? WhyTheTabsAreNotChecked(string ui) =>
        ScriptCode.CheckThenMarker(ui, $"Assert-True ((@($f.tabs) -join '|') -eq '{string.Join('|', Tabs)}') \"", "ui-three-tabs", "if (Test-Path $smokeFile) {")
            ? null
            : "ci/m4-ui.ps1 does not check the window's tabs once, as a statement of its own where it reads smoke.json, with its marker next";

    /// <summary>
    /// The tabs pin fails for each of these edits that keep the UI test's tabs check as text: commented out, in a block
    /// comment or a here-string, or moved into a branch (one that never runs, or one that may not). Each changes a copy of
    /// the real script around the check line; the old pin, which looked for the text, would still find it.
    /// </summary>
    [Theory]
    [InlineData("if ($false) {", "}")]
    [InlineData("<#", "#>")]
    [InlineData("#", "")]
    [InlineData("if ($f.tabs) {", "}")]
    [InlineData("$null = @'", "'@")]
    public void TheTabsPinFailsOnATabsCheckCommentedOutQuotedOrMovedIntoABlock(string before, string after)
    {
        string ui = File.ReadAllText(Path.Combine(CiFolder(), "m4-ui.ps1"));
        var line = Regex.Match(ui, @"(?m)^[ \t]*Assert-True \(\(@\(\$f\.tabs\) .*$");
        Assert.True(line.Success, "the tabs check is not a line of ci/m4-ui.ps1");
        string changed = before == "#"
            ? ui[..line.Index] + "# " + line.Value + ui[(line.Index + line.Length)..]
            : ui[..line.Index] + before + "\n" + line.Value + "\n" + after + ui[(line.Index + line.Length)..];
        Assert.Null(WhyTheTabsAreNotChecked(ui));
        Assert.Contains($"'{string.Join('|', Tabs)}'", changed); // what the old pin looked for, still there as text
        PinFails(() => WhyTheTabsAreNotChecked(changed));
    }

    /// <summary>The tabs pin also fails when the check's marker is not right after it: taken out, or with a statement between them.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("$null = 1\nWrite-Host 'CHECK-RAN: ui-three-tabs'")]
    public void TheTabsPinFailsWhenTheMarkerIsNotRightAfterTheCheck(string marker)
    {
        string ui = File.ReadAllText(Path.Combine(CiFolder(), "m4-ui.ps1"));
        var line = Regex.Match(ui, @"(?m)^[ \t]*Write-Host 'CHECK-RAN: ui-three-tabs'[ \t]*\r?$");
        Assert.True(line.Success, "the tabs check's marker is not a line of ci/m4-ui.ps1");
        Assert.Null(WhyTheTabsAreNotChecked(ui));
        PinFails(() => WhyTheTabsAreNotChecked(ui[..line.Index] + marker + ui[(line.Index + line.Length)..]));
    }

    /// <summary>
    /// The pages pin fails for each of these edits that keep the soak's page assertions as text: in a block comment or a
    /// here-string, or moved into a branch or a loop (one that never runs, or always does); either assertion alone commented
    /// out or moved out of the -Ui part; or a marker taken out, or moved away from its assertion. Each changes a copy of the
    /// real script around its two assertions and their markers.
    /// </summary>
    [Theory]
    [InlineData("if ($false) {", "}")]
    [InlineData("<#", "#>")]
    [InlineData("if ($true -or $Ui) {", "}")]
    [InlineData("while ($false) {", "}")]
    [InlineData("if (0) {", "}")]
    [InlineData("if (-not $true) {", "}")]
    [InlineData("$null = @'", "'@")]
    [InlineData("foreach ($x in @()) {", "}")]
    public void ThePagesPinFailsOnAPageAssertionCommentedOutQuotedOrMovedIntoABlock(string before, string after)
    {
        string soak = File.ReadAllText(Path.Combine(CiFolder(), "soak.ps1"));
        var stages = SoakWalk.Stages(Tabs.Length);
        var all = Regex.Match(soak, @"(?m)^[ \t]*Assert-True \(@\(\$walked .*\r?\n[ \t]*Write-Host 'CHECK-RAN: soak-walked-pages-refreshed'\r?\n" +
            @"[ \t]*Assert-True \(@\(\$uiPages .*\r?\n[ \t]*Write-Host 'CHECK-RAN: soak-no-other-page-refreshed'\r?$");
        Assert.True(all.Success, "the two page assertions and their markers are not on consecutive lines of ci/soak.ps1");
        string changed = soak[..all.Index] + before + "\n" + all.Value + "\n" + after + soak[(all.Index + all.Length)..];
        Assert.Null(WhyThePagesAreNotChecked(soak, stages));
        PinFails(() => WhyThePagesAreNotChecked(changed, stages));
        // Either assertion alone, commented out or moved out of the -Ui part, fails it too.
        string commented = Regex.Replace(soak, @"(?m)^([ \t]*)(Assert-True \(@\(\$uiPages )", "$1# $2");
        Assert.NotEqual(soak, commented);
        PinFails(() => WhyThePagesAreNotChecked(commented, stages));
        PinFails(() => WhyThePagesAreNotChecked(soak[..all.Index] + soak[(all.Index + all.Length)..] + "\n" + all.Value + "\n", stages));
        // So does a marker taken out, or put after the other assertion.
        string noMarker = Regex.Replace(soak, @"(?m)^[ \t]*Write-Host 'CHECK-RAN: soak-walked-pages-refreshed'\r?\n", "");
        Assert.NotEqual(soak, noMarker);
        PinFails(() => WhyThePagesAreNotChecked(noMarker, stages));
        PinFails(() => WhyThePagesAreNotChecked(noMarker.Replace("Write-Host 'CHECK-RAN: soak-no-other-page-refreshed'",
            "Write-Host 'CHECK-RAN: soak-no-other-page-refreshed'\n        Write-Host 'CHECK-RAN: soak-walked-pages-refreshed'", StringComparison.Ordinal), stages));
    }

    /// <summary>
    /// The pages pin fails when another assignment in the script's code sets <c>$walked</c> before the page assertions: to
    /// the pages the soak read (so that both assertions compare those pages with themselves), with a scope, one page put in
    /// place of another by an index, or in a multiple assignment. The list's own statement and the assertions are still as
    /// the pin holds them, so only the count of the list's assignments fails it.
    /// </summary>
    [Theory]
    [InlineData("if ($Ui) { $walked = $uiPages }")]
    [InlineData("$script:walked = @('ui.simple')")]
    [InlineData("$walked[1] = 'ui.simple'")]
    [InlineData("$walked, $rest = @('ui.simple'), $null")]
    [InlineData("${walked} = @('ui.simple')")]
    [InlineData("${script:walked} = @('ui.simple')")]
    public void ThePagesPinFailsWhenAnotherAssignmentSetsTheWalkedList(string added)
    {
        string soak = File.ReadAllText(Path.Combine(CiFolder(), "soak.ps1"));
        var stages = SoakWalk.Stages(Tabs.Length);
        Assert.Null(WhyThePagesAreNotChecked(soak, stages));
        const string asserts = "if ($Assert) {", ui = "if ($Ui) {";
        int at = soak.IndexOf(asserts, StringComparison.Ordinal);
        Assert.True(at >= 0 && soak.IndexOf(asserts, at + 1, StringComparison.Ordinal) < 0, "the -Assert block is not a line of ci/soak.ps1 once");
        string changed = soak[..at] + added + "\r\n" + soak[at..];
        Assert.Single(ScriptCode.StatementsIn(changed, "$walked = @("));
        Assert.All(PageChecks, c => Assert.True(ScriptCode.CheckThenMarker(changed, $"Assert-True ({c.Condition}) \"", c.Id, asserts, ui)));
        Assert.Contains("assigns $walked 2 times", WhyThePagesAreNotChecked(changed, stages));
    }

    /// <summary>
    /// The UI soak stops the walk before its end-of-run screenshots (the step due with them put the window back on the
    /// Simple view under every shot). The window and the runner need the whole app, so their source is read.
    /// </summary>
    [Fact]
    public void TheSoakStopsTheWalkBeforeItsScreenshots()
    {
        string ui = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui");
        var smoke = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(ui, "UiRunner.cs")), "private static async Task<(List<string> files, object stats)> Smoke(")
            .Split('\n').Select(l => l.Trim()).ToList();
        int stop = smoke.FindIndex(l => l.StartsWith("app.Form.StopSoakCycle();", StringComparison.Ordinal));
        int shots = smoke.FindIndex(l => l.StartsWith("files.AddRange(app.Form.Screenshots(dir));", StringComparison.Ordinal));
        Assert.True(stop >= 0 && shots > stop, "the runner does not stop the walk before the screenshots");
        var stopCycle = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(ui, "MainForm.cs")), "public void StopSoakCycle()")
            .Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("_soakWalk?.Stop();", stopCycle); // a step already queued does nothing
        Assert.Contains("_soakTimer?.Stop();", stopCycle);
    }

    /// <summary>
    /// The end-of-run walk draws About first, then Report an issue, each of which reads PoolWatch's own cost as it opens:
    /// right after it stops the soak's walk and reads the meter, before the main window's screenshots and every other
    /// window it builds. Drawn after them, About read the working set, the CPU and the writes that the 8 windows (16
    /// screenshots) made in about 4 s had added: 127 to 131 MB at the end of the UI soak, where the steady peak was 86 to 96 MB, as over budget.
    /// The runner needs the whole app, so its source is read (its code, comments out).
    /// </summary>
    [Fact]
    public void TheEndOfRunWalkDrawsAboutThenReportAnIssueBeforeAnyOtherWindow()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "UiRunner.cs"));
        var smoke = MainWindowLayoutTests.Block(ScriptCode.CSharp(source), "private static async Task<(List<string> files, object stats)> Smoke(")
            .Split('\n').Select(l => l.Trim()).ToList();
        int Line(string start)
        {
            var at = Enumerable.Range(0, smoke.Count).Where(i => smoke[i].StartsWith(start, StringComparison.Ordinal)).ToList();
            Assert.True(at.Count == 1, $"the walk has {at.Count} lines that start \"{start}\", not one");
            return at[0];
        }
        int[] order =
        [
            Line("app.Form.StopSoakCycle();"),
            Line("var meter = engine.Meter.Report();"),
            Line("var about = new AboutDialog("),
            Line("var report = new ReportIssueDialog("),
            Line("files.AddRange(app.Form.Screenshots(dir));"),
            Line("var guide = new SetupWizardForm("),
            Line("var active = new ActiveChangesForm("),
        ];
        Assert.Equal(order.Order().ToArray(), order);
        Assert.Single(Regex.Matches(string.Join("\n", smoke), @"new AboutDialog\("));
    }

    /// <summary>The soak's reads of what About says: the peak working set in MB and the CPU, both of its steady state.</summary>
    private const string AboutPeak = @"peak working set ([0-9]+(?:\.[0-9]+)?) MB \(budget";
    private const string AboutCpu = @"of steady state \([^)]*\): CPU ([0-9]+(?:\.[0-9]+)?)% of one core \(budget";

    /// <summary>
    /// The UI soak holds what the About drawn first in its end-of-run walk says to the budget: its peak working set under
    /// 100 MB and its CPU under 0.5% of one core, as its text gives them, and no "OVER BUDGET" (its rolling line's). Each
    /// read of the text is a statement of the script's top level and the only assignment of its variable; the three checks
    /// are a run in the -Ui part of the -Assert block with their marker after the last. And the reads find About's steady
    /// figures in what About writes (<see cref="AboutDialog.OverheadText"/>), not the rolling line's CPU, and nothing in a
    /// text that judges nothing yet or gives the peak in GB, so that the checks fail.
    /// </summary>
    [Fact]
    public void TheSoakHoldsWhatTheAboutDrawnFirstSaysToTheBudget()
    {
        string soak = File.ReadAllText(Path.Combine(CiFolder(), "soak.ps1"));
        Assert.Equal((1, 1), (ScriptCode.PowerShellAssignments(soak, "aboutPeakMB"), ScriptCode.PowerShellAssignments(soak, "aboutCpu")));
        Assert.Equal(["$aboutPeakMB = if ($aboutText -match '" + AboutPeak + "') { [double]$Matches[1] } else { $null }"], ScriptCode.StatementsIn(soak, "$aboutPeakMB = "));
        Assert.Equal(["$aboutCpu = if ($aboutText -match '" + AboutCpu + "') { [double]$Matches[1] } else { $null }"], ScriptCode.StatementsIn(soak, "$aboutCpu = "));
        Assert.True(ScriptCode.CheckThenMarker(soak,
        [
            "Assert-True ($null -ne $aboutPeakMB -and $aboutPeakMB -lt 100) \"",
            "Assert-True ($null -ne $aboutCpu -and $aboutCpu -lt 0.5) \"",
            "Assert-True ($aboutText -notlike '*OVER BUDGET*') \"",
        ], "soak-about-judged", "if ($Assert) {", "if ($Ui) {"), "ci/soak.ps1 does not hold About's text to the budget in its -Ui checks, with the marker after the last");

        // What About writes, read as the soak reads it: PowerShell's -match is .NET's regular expressions, without case.
        static string? Read(string pattern, string text) => Regex.Match(text, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        var steady = new OverheadReport
        {
            WallSeconds = 2700, JudgedFromSeconds = 900, JudgedSeconds = 1800, Judged = true, JudgedCpuPercentOfOneCore = 0.263,
            JudgedPeakWorkingSetBytes = 90_387_251, JudgedDiskWriteBytesPerHour = 1_800_000, // 86.2 MB, 1.7 MB/h
        };
        const string rolling = "PoolWatch itself (last 10 min): CPU 0.71% of one core, 99 MB now, 4.1 MB/h written";
        string about = AboutDialog.OverheadText(steady, () => rolling);
        Assert.Equal(("86.2", "0.26"), (Read(AboutPeak, about), Read(AboutCpu, about)));
        Assert.Null(Read(AboutCpu, rolling));
        Assert.Null(Read(AboutPeak, AboutDialog.OverheadText(steady with { JudgedPeakWorkingSetBytes = 2L << 30 }, () => rolling)));
        string early = AboutDialog.OverheadText(steady with { JudgedSeconds = 60 }, () => rolling);
        Assert.Null(Read(AboutPeak, early));
        Assert.Null(Read(AboutCpu, early));
    }

    /// <summary>
    /// The soak's line for the samples from outside after the steady window says what they are: they run until PoolWatch
    /// exited, 30 s apart, so the last of them is usually a few seconds before the end-of-run walk began ("about": the gap
    /// leaves out PoolWatch's own start before its timer). It read "End of run", as if those samples were the walk's.
    /// </summary>
    [Fact]
    public void TheSoakSaysItsLastSamplesFromOutsideComeBeforeTheEndOfRunWalk()
    {
        string soak = File.ReadAllText(Path.Combine(CiFolder(), "soak.ps1"));
        Assert.Contains("[void]$md.AppendLine((\"- After the steady window (samples from outside from {0:N1} min until PoolWatch exited; not judged): {1}\" -f ($steadyTo / 60), $endText))", soak);
        Assert.Contains("over {2} samples, the last about {3:N0} s {4}' -f", soak);
        Assert.Contains("$endWhat = if ($Ui) { 'the end-of-run screenshots and checks' } else { 'the reports written at exit' }", soak);
        Assert.DoesNotContain("- End of run (", soak);
    }
}
