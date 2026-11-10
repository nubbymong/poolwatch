using System.Text.Json;
using PoolWatch.Core.Incidents;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// What the report folders' write budget spent and held back is visible: in About, under PoolWatch's own overhead, and in
/// cache\profile.json's <c>writeBudget</c>, so a deferral is never silent; and neither claims more than the figures hold.
/// </summary>
public sealed class WriteBudgetVisibilityTests
{
    private static AboutDialog.BudgetFigures Figures(long spent, long must, long canWait, long allowance, long held) =>
        new(spent, must, canWait, allowance, held, BytesPerHour: 1L << 20, Burst: 256L << 10);

    private static readonly TimeSpan Chart = TimeSpan.FromMinutes(30);

    [Fact]
    public void AboutSaysWhatTheReportFoldersWroteAndWhatTheBudgetHoldsBack()
    {
        string none = AboutDialog.WriteBudgetText(Figures(3L << 20, 2L << 20, 1L << 20, 2L << 20, held: 0), FoldersBehind.None, Chart);
        Assert.Equal("Report folders (incidents and spawn storms): 3 MB written since PoolWatch started. 2 MB of it was what a reader needed at once " +
                     "(an incident opening, re-opening or closing, a trace, a new storm folder), written whatever the budget; 1 MB was writes that could " +
                     "wait, against their budget of 1 MB an hour (256 KB burst): within the 2 MB it allows them so far. No write that could wait was held " +
                     "back. No open incident's report is behind now; no open incident's chart and CSV files are 30 min old or more.", none);
        string behind = AboutDialog.WriteBudgetText(Figures(5L << 20, 4L << 20, 1L << 20, 2L << 20, held: 1234),
            new FoldersBehind(3, TimeSpan.FromMinutes(42), 2, TimeSpan.FromMinutes(75), 1), Chart);
        Assert.Contains("Writes that could wait were held back 1,234 times, each for a later sample (each time one was due).", behind);
        Assert.Contains(" 3 open incidents' reports are behind now, the oldest last written 42 min ago (1 of them because its last write failed: poolwatch.log says why)", behind);
        Assert.EndsWith("; 2 open incidents' charts and CSV files are 30 min old or more (written while the budget has room to spare), the oldest 1 h 15 min old " +
                        "(the window's details of an open incident are live).", behind);
        string one = AboutDialog.WriteBudgetText(Figures(0, 0, 0, 512L << 10, held: 1), new FoldersBehind(1, TimeSpan.FromMinutes(6), 1, TimeSpan.FromMinutes(31), 2), Chart);
        Assert.Contains("A write that could wait was held back once, for a later sample.", one);
        Assert.Contains("1 open incident's report is behind now", one);
        Assert.Contains("(2 of them because their last write failed", one);
        Assert.Contains("; 1 open incident's chart and CSV files are 30 min old or more", one);
        // About shows it with the overhead, read as it opens.
        string about = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "AboutDialog.cs"));
        Assert.Contains("Overhead(engine) + \"\\n\" + ReportFolders(analysis)", about);
    }

    /// <summary>
    /// The writes a reader needs at once are made whatever the budget, so all that was written can be far over 1 MB an hour
    /// (9144b20's test measured 1.68 MB in the hour ten incidents opened): About never says the folders kept within their
    /// budget then. It holds what could wait to what the budget allows it, and says "over" when that is over.
    /// </summary>
    [Fact]
    public void AboutNeverSaysTheFoldersKeptWithinTheirBudgetWhenTheyDidNot()
    {
        string busy = AboutDialog.WriteBudgetText(Figures(spent: 1_761_607, must: 1_300_000, canWait: 461_607, allowance: 1_572_864, held: 40), FoldersBehind.None, Chart);
        Assert.DoesNotContain("1.7 MB written since PoolWatch started, within", busy);
        Assert.Contains("1.7 MB written since PoolWatch started. 1.2 MB of it was what a reader needed at once", busy);
        Assert.Contains(": within the 1.5 MB it allows them so far.", busy);
        string over = AboutDialog.WriteBudgetText(Figures(spent: 3L << 20, must: 0, canWait: 3L << 20, allowance: 2L << 20, held: 0), FoldersBehind.None, Chart);
        Assert.Contains("3 MB was writes that could wait, against their budget of 1 MB an hour (256 KB burst): over the 2 MB it allows them so far.", over);
    }

    [Fact]
    public void ProfileJsonSaysWhatTheWriteBudgetSpentAndHeldBack()
    {
        var b = new WriteBudget();
        b.Spend(300L << 10, 1000, must: true);
        b.Spend(200L << 10, 3_601_000);
        b.Hold();
        b.Hold();
        var json = JsonSerializer.SerializeToElement(CostReport.WriteBudgetOf(b, new FoldersBehind(2, TimeSpan.FromMinutes(31.5), 3, TimeSpan.FromMinutes(64), 1), elapsedSeconds: 7200));
        Assert.Equal(1L << 20, json.GetProperty("bytesPerHour").GetInt64());
        Assert.Equal(256L << 10, json.GetProperty("burstBytes").GetInt64());
        Assert.Equal(0.488, json.GetProperty("spentMB").GetDouble());
        Assert.Equal(0.244, json.GetProperty("spentMBPerHour").GetDouble());
        Assert.Equal(0.293, json.GetProperty("mustMB").GetDouble());
        Assert.Equal(0.195, json.GetProperty("canWaitMB").GetDouble());
        Assert.Equal(1.5, json.GetProperty("canWaitAllowanceMB").GetDouble()); // an hour's refill and two bursts
        Assert.Equal(2, json.GetProperty("heldBack").GetInt64());
        Assert.Equal(2, json.GetProperty("incidentsBehindNow").GetInt32());
        Assert.Equal(31.5, json.GetProperty("oldestBehindMinutes").GetDouble());
        Assert.Equal(1, json.GetProperty("writesFailedNow").GetInt32());
        Assert.Equal(3, json.GetProperty("chartsBehindNow").GetInt32());
        Assert.Equal(64, json.GetProperty("oldestChartMinutes").GetDouble());
        string cost = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "CostReport.cs"));
        Assert.Contains("writeBudget = WriteBudgetOf(analysis.Incidents.Budget, behind, elapsed),", cost);
        Assert.Contains("handleScansNotWritten = engine.HandleScansNotWritten,", cost);
        // The CI's agentic-load check holds what could wait to the allowance profile.json gives, not to a bound of its own.
        string soak = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "agentic-soak.ps1"));
        Assert.Contains("$canWaitAllowed = [double]$profile.writeBudget.canWaitAllowanceMB", soak);
    }
}
