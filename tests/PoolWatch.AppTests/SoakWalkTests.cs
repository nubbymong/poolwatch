using System.Text.RegularExpressions;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The CI UI soak's walk through the main window (<c>SoakWalk</c>, <c>MainForm.StartSoakCycle</c>) and what
/// <c>ci/soak.ps1</c> expects of it: the Simple view and every tab of the Advanced view, one a minute, each refresh
/// profiled for the view on screen, and nothing more once it is stopped for the end-of-run screenshots.
/// </summary>
public class SoakWalkTests
{
    /// <summary>The Advanced view's tabs, as <c>ci/m4-ui.ps1</c> checks them on the window the CI UI test opens.</summary>
    private static readonly string[] Tabs = ["Pool causes", "Services", "Tasks and startup apps"];

    private static string CiFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return Path.Combine(dir.FullName, "ci");
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

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
        Assert.Contains($"'{string.Join('|', Tabs)}'", File.ReadAllText(Path.Combine(ci, "m4-ui.ps1")));
        var stages = SoakWalk.Stages(Tabs.Length);
        Assert.Equal(["ui.simple", "ui.page0", "ui.page1", "ui.page2"], stages);

        // ci/soak.ps1 names them in one list, and both of its page assertions use it: each must be refreshed, and no other.
        string soak = File.ReadAllText(Path.Combine(ci, "soak.ps1"));
        var lines = soak.Split('\n').Select(l => l.Trim()).ToList();
        string line = lines.Single(l => l.StartsWith("$walked = @(", StringComparison.Ordinal));
        Assert.Equal(stages, Regex.Matches(line, "'([^']*)'").Select(m => m.Groups[1].Value).ToList());
        // Each assertion is the whole condition of its own Assert-True, in the -Ui part of the -Assert block: not commented
        // out, and not inside a condition that is always true (a "$true -or ..." in front of it).
        int asserts = lines.IndexOf("if ($Assert) {");
        int ui = lines.FindIndex(asserts + 1, l => l == "if ($Ui) {");
        Assert.True(asserts >= 0 && ui > asserts, "ci/soak.ps1 has no -Ui part in its -Assert block");
        foreach (var condition in new[] { "@($walked | Where-Object { $_ -notin $uiPages }).Count -eq 0", "@($uiPages | Where-Object { $_ -notin $walked }).Count -eq 0" })
        {
            string assertion = $"Assert-True ({condition}) \"";
            var at = Enumerable.Range(0, lines.Count).Where(i => lines[i].StartsWith(assertion, StringComparison.Ordinal)).ToList();
            Assert.True(at.Count == 1 && at[0] > ui, $"ci/soak.ps1 does not assert {condition} once, on its own, in its -Ui checks");
        }
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
        int shots = smoke.FindIndex(l => l.StartsWith("var files = app.Form.Screenshots(dir);", StringComparison.Ordinal));
        Assert.True(stop >= 0 && shots > stop, "the runner does not stop the walk before the screenshots");
        var stopCycle = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(ui, "MainForm.cs")), "public void StopSoakCycle()")
            .Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("_soakWalk?.Stop();", stopCycle); // a step already queued does nothing
        Assert.Contains("_soakTimer?.Stop();", stopCycle);
    }
}
