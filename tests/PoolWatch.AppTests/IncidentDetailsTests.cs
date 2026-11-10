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
    /// (but for the confidence); an open incident's both from memory (UiData.Live, built as its report.json would be now),
    /// a closed one's from its report.json; Export report writes an open incident's folder whole first. Read from the
    /// window's source, as the window needs the whole app to build. (Before 1.0.0 both read report.json alone, which the
    /// write budget can leave about an hour behind an open incident on a busy PC: <c>LiveDetailsTests</c>.)
    /// </summary>
    [Fact]
    public void TheDetailsShowTheSummaryForThePaneAndTheCopiesCopyTheSummary()
    {
        string ui = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui");
        string main = File.ReadAllText(Path.Combine(ui, "MainForm.cs")), actions = File.ReadAllText(Path.Combine(ui, "UiActions.cs"));
        string details = MainWindowLayoutTests.Block(main, "private string IncidentDetails(CauseRow row)");
        Assert.Contains("UiActions.IncidentDetails(dir)", details);
        Assert.Contains("if (UiData.Live(_analysis, dir) is { } live)", details);
        Assert.Contains("CopyText.IncidentDetails(live.Json)", details);
        Assert.True(details.IndexOf("UiData.Live(", StringComparison.Ordinal) < details.IndexOf("UiActions.IncidentDetails(dir)", StringComparison.Ordinal),
            "an open incident's details from memory before any report.json is read");
        Assert.DoesNotContain("IncidentSummary", details);
        Assert.Contains("() => _actions.IncidentSummaryNow(dir)", MainWindowLayoutTests.Block(main, "private CopyInfo? CauseCopy(object? key)"));
        Assert.Contains("public string IncidentSummaryNow(string dir) => UiData.Live(_analysis, dir) is { } live ? CopyText.IncidentSummary(live.Json) : IncidentSummary(dir);", actions);
        Assert.Contains("Copy(IncidentSummaryNow(dir))", MainWindowLayoutTests.Block(actions, "public void CopyLatestIncidentSummary(IWin32Window? owner)"));
        string export = MainWindowLayoutTests.Block(actions, "public void ExportReport(IWin32Window? owner, string? dir)");
        int write = export.IndexOf("UiData.WriteOpenIncident(_analysis, dir) is false", StringComparison.Ordinal);
        Assert.True(write > 0 && write < export.IndexOf("Exporter.Export(", StringComparison.Ordinal), "an open incident's folder is written before it is zipped");
    }

    /// <summary>An open incident's details say they are live, when its folder was last written, and why it can be behind them.</summary>
    [Fact]
    public void AnOpenIncidentsDetailsSayTheyAreLiveAndWhenItsFolderWasWritten()
    {
        var written = new DateTime(2026, 11, 9, 23, 14, 0, DateTimeKind.Utc);
        string note = MainForm.LiveFolderNote(written);
        Assert.StartsWith("These details are live, from PoolWatch's memory.", note);
        Assert.Contains($"(its report was last written at {PoolWatch.Core.Detection.Fmt.Stamp(written)})", note);
        Assert.Contains("the timeline chart and the CSV files when it has room to spare", note);
        Assert.Contains("Export report writes the folder whole first.", note);
        Assert.Contains("(its report is being written)", MainForm.LiveFolderNote(null));
    }

    /// <summary>
    /// Report an issue's incident digest of an open incident comes from the same live summary as its details (so the window
    /// never shows two states of one incident), before any report.json is read; a closed one's from its report.json. The
    /// digest of the live summary is report.json's once written (<c>LiveDetailsTests.ReportAnIssuesDigestOfAnOpenIncidentIsTheLiveOne</c>).
    /// </summary>
    [Fact]
    public void ReportAnIssuesDigestOfAnOpenIncidentIsBuiltFromMemory()
    {
        string dialog = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "ReportIssueDialog.cs"));
        string digest = MainWindowLayoutTests.Block(dialog, "internal static string? Digest(AnalysisPipeline analysis, string? incidentDir)");
        int live = digest.IndexOf("if (UiData.Live(analysis, incidentDir) is { } live) return IssueReport.IncidentDigest(live.Json);", StringComparison.Ordinal);
        Assert.True(live > 0 && live < digest.IndexOf("\"report.json\"", StringComparison.Ordinal), "an open incident's digest from memory before any report.json is read");
        Assert.Contains("_incident = Digest(analysis, incidentDir);", dialog);
    }
}
