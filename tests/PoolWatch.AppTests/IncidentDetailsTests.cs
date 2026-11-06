using PoolWatch.Core.Util;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// An incident's summary in the Pool causes details, and in Copy summary: the details show it without Markdown marks and
/// with local times (<see cref="CopyText.IncidentDetails"/>); Copy summary and the tray copy it as report.json has it,
/// but for each suspect's confidence, which both give in the Confidence column's words (<see cref="CopyText.IncidentSummary"/>).
/// </summary>
public sealed class IncidentDetailsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-details-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Report = """
        { "id": "20261031-191052-Ntfc-6366744e", "kind": "Growth", "tag": "Ntfc",
          "headline": "`Ntfc` (ntfs.sys CCB_DATA) grew 4 MB in 29 s; top suspect Leaker.exe PID 1512, started 19:10:26 (strongly correlated)",
          "window": { "fromUtc": "2026-10-31T19:10:12.7734278Z", "onsetUtc": "2026-10-31T19:10:22.7705869Z", "toUtc": "2026-10-31T19:10:52.7693026Z" } }
        """;

    /// <summary>The window reads an incident folder's report.json for the details one way, and for Copy summary the other.</summary>
    [Fact]
    public void AnIncidentFolderReadsForTheDetailsWithoutMarkdownAndForCopySummaryAsItIs()
    {
        File.WriteAllText(Path.Combine(_dir, "report.json"), Report);
        string pane = UiActions.IncidentDetails(_dir), copy = UiActions.IncidentSummary(_dir);
        Assert.Equal(CopyText.IncidentDetails(Report), pane);
        Assert.StartsWith("Ntfc (ntfs.sys CCB_DATA) grew 4 MB", pane);
        Assert.DoesNotContain("2026-10-31T19:10:12.7734278Z", pane);
        Assert.Equal(CopyText.IncidentSummary(Report), copy);
        Assert.StartsWith("`Ntfc` (ntfs.sys CCB_DATA) grew 4 MB", copy);
        Assert.Contains("Window: 2026-10-31T19:10:12.7734278Z to 2026-10-31T19:10:52.7693026Z (onset 2026-10-31T19:10:22.7705869Z)", copy);
    }

    /// <summary>
    /// The details show an incident's summary for the pane, and Copy summary and the tray copy it as report.json has it
    /// (but for the confidence). Read from the window's source, as the window needs the whole app to build.
    /// </summary>
    [Fact]
    public void TheDetailsShowTheSummaryForThePaneAndTheCopiesCopyTheSummary()
    {
        string ui = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui");
        string main = File.ReadAllText(Path.Combine(ui, "MainForm.cs")), actions = File.ReadAllText(Path.Combine(ui, "UiActions.cs"));
        string details = MainWindowLayoutTests.Block(main, "private string IncidentDetails(CauseRow row)");
        Assert.Contains("UiActions.IncidentDetails(dir)", details);
        Assert.DoesNotContain("IncidentSummary", details);
        Assert.Contains("() => UiActions.IncidentSummary(dir)", MainWindowLayoutTests.Block(main, "private CopyInfo? CauseCopy(object? key)"));
        Assert.Contains("Copy(IncidentSummary(dir))", MainWindowLayoutTests.Block(actions, "public void CopyLatestIncidentSummary(IWin32Window? owner)"));
    }
}
