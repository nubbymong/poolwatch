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
    /// Export report of an open incident puts both ends of its events.csv in the zip, built from memory (its file holds up to
    /// the first 500 events of its window while it is open, which follow the window's start), with a report.md that counts
    /// them, after its folder is
    /// written; and its details say what its events.csv holds until it closes (a re-opened incident's too), and that the
    /// export has both ends. Read from the window's source, as the window needs the whole app to build (<c>BothEndsTests</c>
    /// holds the zip's events.csv to both ends and its report.md to it).
    /// </summary>
    [Fact]
    public void AnOpenIncidentsExportTakesBothEndsOfItsEventsCsvFromMemory()
    {
        string ui = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui");
        string export = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(ui, "UiActions.cs")), "public void ExportReport(IWin32Window? owner, string? dir)");
        int write = export.IndexOf("UiData.WriteOpenIncident(_analysis, dir) is false", StringComparison.Ordinal);
        int live = export.IndexOf("var live = UiData.OpenIncidentExportFiles(_analysis, dir);", StringComparison.Ordinal);
        int zip = export.IndexOf("Exporter.Export(", StringComparison.Ordinal);
        Assert.True(write > 0 && write < live && live < zip, "the folder written, then both ends of events.csv and report.md from memory, then the zip");
        Assert.Contains("new Dictionary<string, string> { [AgentsGuide.FileName] = AgentsGuide.Text }, live);", export);
        string snapshot = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(ui, "UiSnapshot.cs")), "public static IReadOnlyDictionary<string, string>? OpenIncidentExportFiles(AnalysisPipeline a, string dir)");
        Assert.Contains("foreach (var i in a.Incidents.Open)", snapshot);
        Assert.Contains("return a.Incidents.ExportFilesNow(a.History, i);", snapshot);
        string note = MainForm.LiveFolderNote(null);
        Assert.Contains("Until then events.csv holds up to the first 500 events from where the incident's window started when the file was begun (fewer while the budget holds " +
                        "rows back; the first PoolWatch still held, when it had already let go of events from there, which the file then says; " +
                        "a re-opened incident's holds both ends as its last close wrote them). PoolWatch measures the growth's start again as it goes on, and the first events " +
                        "follow the window's start, at most every 10 minutes: a file that is still being filled starts again from the new start, one whose last row says to when it holds its events stays " +
                        "as written until the incident closes, and report.md says where the file's rows are from and where the window starts now whenever the two differ. " +
                        "When the incident closes with more events than those first 500, PoolWatch replaces the file with both ends of the window as it is measured then: " +
                        "the first events and the latest up to the window's end, up to 500 of each, and a " +
                        "row between them saying how many events were left out, if any were.", note);
        Assert.Contains("Its zip has both ends of events.csv, from memory, with a report.md that counts them.", note);
        // The times the note names are of the file and of the window as it is now: none of an opening the file may no longer start at.
        Assert.DoesNotContain("when it opened", note);
        Assert.DoesNotContain("first noticed", note);
    }

    /// <summary>
    /// The changelog, the design and technical pages and the guide for AI assistants say what an incident's events.csv
    /// holds: both ends of the window its report gives. Its first part follows the window's start, which moves as the
    /// onset is measured again (later for a long, steady growth, sometimes earlier), a closed incident's at once and an
    /// open one's at most every 10 minutes; each says why (left where the incident opened, the first part would be mostly
    /// from before the growth: 416 of its 500 rows on average after an hour, as <c>BothEndsTests</c> measures), and that
    /// when PoolWatch no longer holds every event from the start, the first part is the first events it still holds
    /// from there, up to 500: the window's first when the incident had kept them itself from a start measured later
    /// (the file then says how many), and otherwise said as the first PoolWatch still held, with the time up to which
    /// it had let events go. None says any more that the first part stays where
    /// the incident opened, nor calls it what was happening when PoolWatch first noticed the growth
    /// (<c>IncidentPersistenceTests</c> and <c>BothEndsTests</c> hold the file and the report to what these say).
    /// </summary>
    [Fact]
    public void TheDocsSayAnIncidentsFirstEventsFollowItsWindowsStartAndWhy()
    {
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        var docs = new (string Name, string Text, string Follows, string Why, string Earlier, string StillHeld)[]
        {
            ("CHANGELOG.md", Doc("CHANGELOG.md"),
                "the first 500 follow the window's start: they are taken again from where the window starts then (when the incident closes, and at most every 10 minutes " +
                "while it is open), so the file shows the window the report shows.",
                "Left where the incident opened, they would be mostly from before the growth after an hour on a busy PC (416 of the 500, on average, in a test of that load).",
                "later for a long, steady one and sometimes earlier",
                "the file keeps the first events it still holds from there, up to 500: where those are the window's first events (the report had kept them itself, from a " +
                "start now measured later) there can be fewer than 500 and the file says how many, and where PoolWatch had let go of events from the start on, the file " +
                "and `report.md` say so, with the time up to which it had let events go."),
            ("DESIGN.md", Doc("docs", "DESIGN.md"),
                "So the first part follows the window's start (decided 2026-11-11): a person reads the report's window and expects `events.csv` to show that window, and a " +
                "first part left where the incident opened was mostly from before the growth.",
                "416 of them on average after an hour in `BothEndsTests`",
                "(later, or earlier: a second rule that fires with an earlier onset, or the first samples after the opening)",
                "the first part is the first events PoolWatch still holds from it, up to 500: the incident's own, when its first part holds an event from the new start on " +
                "that the buffer has let go (it keeps those, and lets go of the ones from before the start: after a start measured later they are the window's first " +
                "events, often fewer than 500, and the file says how many, \"events.csv keeps the first 200 events from ...\"), else the first 500 the buffer still holds. " +
                "Where PoolWatch had let go of events from the new start on, the incident notes up to when (`KeptEvents.LostTo`), which the file and the report then say."),
            ("TECHNICAL.md", Doc("docs", "TECHNICAL.md"),
                "the first part follows the window's start: it is taken again from there, out of the events PoolWatch holds (`IncidentManager.Follow`), a closed incident's " +
                "at once (at its close, or when a trace attached measures it again), an open one's at most every 10 minutes, and at once for an export.",
                "a first part left where the incident opened would be mostly from before the growth (after an hour on a busy PC, 416 of its 500 rows on average, `BothEndsTests`).",
                "later as a long, steady growth's rise grows, and sometimes earlier",
                "the first part is the first events it still holds from there, up to 500. After a start measured later those can be the incident's own first events from the " +
                "new start on, which are then the window's first, often fewer than 500, and the file says how many; where PoolWatch had let go of events from the start " +
                "on, the file's `Note` and `Gap` rows and `report.md` say so, with the time of the latest event it had let go by then"),
            ("AGENTS.md", Doc("src", "PoolWatch", "Assets", "AGENTS.md"),
                "the first part follows the window's start: it is taken again from where the window starts then, a closed incident's at once and an open one's at most " +
                "every 10 minutes, so the file shows the window the report gives.",
                "(Left where the incident opened, the first part would be mostly from before the growth: after an hour on a busy PC, 416 of its 500 rows on average.)",
                "(later as a long, steady growth's rise grows, sometimes earlier)",
                "the first part is then the first events it still holds from there, up to 500. After a start measured later those can be the first events the incident " +
                "had kept itself from the new start on: they are the window's first, often fewer than 500, and the file says how many. Where PoolWatch had let go of " +
                "events from the start on, the `Note` row, the `Gap` row and `report.md` say so, with the time of the latest event it had let go by then"),
        };
        foreach (var (name, text, follows, why, earlier, stillHeld) in docs)
        {
            Assert.True(text.Contains(follows, StringComparison.Ordinal), name + ": the first part follows the window's start");
            Assert.True(text.Contains(why, StringComparison.Ordinal), name + ": why it follows");
            Assert.True(text.Contains(earlier, StringComparison.Ordinal), name + ": the onset can be measured earlier");
            Assert.True(text.Contains(stillHeld, StringComparison.Ordinal), name + ": the first events PoolWatch still holds, up to 500, and what the file then says");
            Assert.True(text.Contains("(by then it had already let go of events up to", StringComparison.Ordinal) || name == "CHANGELOG.md", name + ": up to when it had let events go");
            // None says any more that the file always keeps 500 then (one branch keeps fewer, and says how many).
            Assert.False(text.Contains("the first 500 it still holds from there", StringComparison.Ordinal), name + " says the first part is always the first 500 still held");
            Assert.False(text.Contains("the first part is the first 500 PoolWatch still holds from it", StringComparison.Ordinal), name + " says the first part is always the first 500 still held");
        }
        Assert.Contains("say so, with the time up to which it had let events go", docs[0].Text);
        // A closed incident's follows at once, an open one's at most every 10 minutes, exactly while the buffer holds every event from the start.
        string design = docs[1].Text;
        Assert.Contains("A closed incident's first part follows at once (at its close, and at a later write that measured it again: a trace attached), an open one's at most " +
                        "every 10 minutes (each follow takes the whole buffer again, about 14,000 events at the owner's rate, and a long growth's onset moves every few minutes), " +
                        "and at once when an export takes both ends.", design);
        Assert.Contains("The first part is exactly the window's first events while the buffer holds every event from the start", design);
        Assert.Contains("\"events.csv keeps the first 500 events from 2026-11-10T06:01:40.000Z, where the incident's window started when this file was written, and the latest " +
                        "500 up to the window's end\"", design);
        // What each said while the first part stayed where the incident opened (the owner's earlier choice, 2026-11-10).
        foreach (var (name, text) in docs.Select(d => (d.Name, d.Text)).Append(("USER-GUIDE.md", Doc("docs", "USER-GUIDE.md"))).Append(("README.md", Doc("README.md"))))
        {
            Assert.False(text.Contains("first noticed", StringComparison.OrdinalIgnoreCase), name + " names a first-noticed start");
            Assert.False(text.Contains("first-noticed", StringComparison.OrdinalIgnoreCase), name + " names a first-noticed start");
            Assert.False(text.Contains("window started when it opened", StringComparison.Ordinal), name + " gives the window's start at the opening as where the first part starts");
            Assert.False(text.Contains("window started when the incident opened", StringComparison.Ordinal), name + " gives the window's start at the opening as where the first part starts");
            Assert.False(text.Contains("the first part stays where it", StringComparison.Ordinal), name + " says the first part stays");
            Assert.False(text.Contains("500 stay where they were", StringComparison.Ordinal), name + " says the first 500 stay");
            Assert.False(text.Contains("never taken again", StringComparison.Ordinal), name + " says the first part is never taken again");
        }
    }

    /// <summary>
    /// The design and technical pages, the guide for AI assistants and the changelog say where an incident made for a
    /// trace of its own (Capture now) starts: the moment it was made, then, once its trace has finished, exactly the
    /// trace's own start, which is never measured or moved (the owner's choice, 2026-11-11), and what is read, said and
    /// counted from that start: the window its report lists and its events.csv holds starts exactly there, with no 2
    /// minutes before it (the window is the trace, start to end). None keeps the sentence it had while a rising tag's
    /// start was still measured after it was set (up to a minute before a 60 s trace), nor those it had while the window
    /// began 2 minutes before the trace and the file and the report said the start's move in words of their own (the
    /// first part follows the start now, so the file is written from it). <c>IncidentPersistenceTests</c> holds the
    /// incident and its report to what they say
    /// (<c>ATraceThePersonStartedStartsExactlyWhereItsTraceDidOnBothClocksAndIsNeverMoved</c> and the tests beside it).
    /// </summary>
    [Fact]
    public void TheDocsSayAnIncidentMadeForATraceStartsExactlyWhereItsTraceDidAndIsNeverMoved()
    {
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        string design = Doc("docs", "DESIGN.md"), technical = Doc("docs", "TECHNICAL.md"), guide = Doc("src", "PoolWatch", "Assets", "AGENTS.md"), changelog = Doc("CHANGELOG.md");
        Assert.Contains("It is made with its start and its window's end at that moment (the newest sample's time, once PoolWatch has taken a sample), and when its trace " +
                        "finishes its start is set to the trace's own start and its window's end to the trace's own end, exactly, on both clocks " +
                        "(`IncidentManager.ApplyCapture`, `IncidentManager.EventWindowStartMs`, `IncidentManager.EventWindowEndMs`; a trace that did not record leaves both " +
                        "where the incident was made). " +
                        "That is their only move: `IncidentManager.Measure` never moves the start, neither to where the tag's rise began", design);
        Assert.Contains("nor to the next sample's time (before, the window the report listed then started up to a sample later than the start it printed), and", design);
        Assert.Contains("So `report.json`'s `onsetUtc`, the start of the window `report.md` lists (the trace's start itself, to the trace's end, with no minute added) and the " +
                        "start the texts give are one: the tag's levels are read " +
                        "from the first sample at or after it (the last one before it, when the trace ended before the next sample), the end of its rise is looked for from " +
                        "there on only, and what its trace's answer says and counts over the incident's span (\"grew 20 MB in 1 min\", an owner's process starts during it) " +
                        "runs from it to the end of that rise, else to the trace's end (`Incident.SpanEndMs`).", design);
        Assert.Contains("When the trace is attached, the first part follows the start as a closed incident's does, at once, and the file is written again from the trace's " +
                        "start, every row of it (with no event row when the trace's window holds no event: a row from before the trace, written while it recorded, is not left " +
                        "in a closed incident's file); `report.md` gives the window's end", design);
        Assert.Contains("Its start and its window's end are the moment it was made until the trace finishes, and from then on exactly the trace's own start and the trace's " +
                        "own end (a trace that could not record leaves them where they were), which PoolWatch never measures or moves; so", technical);
        Assert.Contains("For a `Capture` incident the start is not measured: its trace is its window. Its start is the moment the incident was made until its trace " +
                        "finishes, and from then on exactly the " +
                        "trace's own start, a little later (a trace that could not record leaves it where it was). PoolWatch never moves it otherwise, so that start is " +
                        "`onsetUtc`, the window `report.md` lists starts exactly there, with no 2 minutes before it, and `startBytes` is the tag's level at the first sample " +
                        "from it on (the last one before it, when the trace ended before the next sample).", guide);
        Assert.Contains("`onsetUtc` (when the growth began; a `Capture` incident's start, as above), `endUtc` (when it stopped; empty while still climbing, and when " +
                        "there was no growth to end)", guide);
        Assert.Contains("A trace you start with **Capture now** gets a report of its own: once the trace has finished, that report starts exactly where the trace started " +
                        "and measures the growth from there", changelog);
        // What each said while the start was still measured after it was set.
        Assert.DoesNotContain("its start is set to the trace's when the trace finishes", design);
        Assert.DoesNotContain("its incident's start is set to the trace's when the trace finishes", technical);
        Assert.DoesNotContain("when the trace finishes it is set to the trace's own start, a little after the incident was made", guide);
        // What each said while the window began 2 minutes before the trace's start, and while the file and the report said the start's move.
        foreach (var (name, text) in new[] { ("DESIGN.md", design), ("TECHNICAL.md", technical), ("AGENTS.md", guide), ("CHANGELOG.md", changelog) })
        {
            Assert.False(text.Contains("2 minutes before the trace", StringComparison.Ordinal), name + " starts the window 2 minutes before the trace");
            Assert.False(text.Contains("from 2 minutes before it", StringComparison.Ordinal), name + " starts the window 2 minutes before the start");
            Assert.False(text.Contains("had been set to", StringComparison.Ordinal), name + " quotes words no row says any more");
            Assert.False(text.Contains("when its trace finished", StringComparison.Ordinal), name + " quotes words report.md no longer says");
        }
    }

    /// <summary>
    /// The same four say where such an incident's window ends: its trace is its window (the owner's words, 2026-11-11),
    /// so it ends exactly where the trace did, with no minute added and whatever its tag did, and its events.csv and the
    /// events report.md lists are the trace's own, from its start to its end, however long the trace was. And the design
    /// and technical pages and the guide say what a closed incident does with an event of its window that comes after
    /// its last write: its report lists it at the next sample (its row added at the end of a file that holds its first
    /// part alone, the rows a write left for later following with the report), and two minutes after the window's end
    /// one more write that must be made gives its events.csv what the report lists, each once and in time order; that
    /// every write of a closed incident that must be made leaves its file with every row it is to hold, in time order;
    /// the design page
    /// says that every incident PoolWatch holds looks at every event before the buffer lets it go and that none leaves
    /// memory while a trace that may be attached to it records or is read; and all three that no event from after the
    /// window's end is counted as left out, with no condition on where the end stays: when the end is measured again
    /// the latest part and the counts are taken again from the events PoolWatch holds, and only where it no longer
    /// holds them do the Gap row and report.md say that the count runs past the window's end.
    /// <c>IncidentPersistenceTests</c> holds the incidents and their folders to what they say
    /// (<c>ATraceThePersonStartedHasItsTraceForAWindowExactlyFromItsStartToItsEnd</c> and the tests after it), and
    /// <c>IncidentRewriteTests</c> that none leaves memory while a trace records, the counts taken again and what the
    /// texts say where they cannot be.
    /// </summary>
    [Fact]
    public void TheDocsSayAnIncidentMadeForATraceEndsExactlyWhereItsTraceDidAndWhatAClosedIncidentDoesWithItsWindowsLateEvents()
    {
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        string design = Doc("docs", "DESIGN.md"), technical = Doc("docs", "TECHNICAL.md"), guide = Doc("src", "PoolWatch", "Assets", "AGENTS.md"), changelog = Doc("CHANGELOG.md");
        // The window's end.
        Assert.Contains("has no growth whose start is to be measured: its trace is its window (the owner's words, 2026-11-11: the window is the trace, start to end), from the " +
                        "trace's own start to the trace's own end, without the 2 minutes before the start that every other incident's window has, and without a minute after " +
                        "the end.", design);
        Assert.Contains("and the window's end is not measured either: it is not a minute after the end of the tag's rise, nor a minute after the incident was made (before, it " +
                        "was: a 5-minute trace's `events.csv` and report held the events of its first minute).", design);
        Assert.Contains("`report.md` gives the window's end as the time the incident closed and as the trace's (\"Recorded 07:05:20-07:10:20\"). While the trace records, the " +
                        "incident holds the events that come after it was made (at most 4,000, as an incident that can re-open does), so the window the trace gives it finds them " +
                        "however often its folder was written meanwhile (`IncidentManager.CanTakeLater`).", design);
        Assert.Contains("has no growth to measure: its trace is its window. Its start and its window's end are the moment it was made until the trace finishes", technical);
        Assert.Contains("so its `events.csv` and the events `report.md` lists run from the trace's start to the trace's end, with no 2 minutes before it and no minute after it, " +
                        "however long the trace was.)", technical);
        Assert.Contains("Its window ends exactly where its trace ended (`closedUtc`, the trace's own `endUtc` under `capture`; the moment the incident was made until the " +
                        "trace finishes, and for a trace that could not record), with no minute added and whatever its tag did: its `events.csv` and the events `report.md` " +
                        "lists are the trace's own, from its start to its end, however long the trace was.", guide);
        Assert.Contains("and its events are the trace's own, from exactly where the trace started to exactly where it ended, however long the trace was (in 1.0.0-beta.2 they " +
                        "began 2 minutes before the report's start and stopped a minute after you asked for the trace, or a minute after the tag stopped rising, so a 5-minute " +
                        "trace's report held the events of its first minute).", changelog);
        // A closed incident and the events of its window that come after its last write.
        Assert.Contains("A closed incident goes on taking events. One timed at or before its window's end that comes after its last write", design);
        Assert.Contains("is taken as it comes (`IncidentManager.AddEvents`), and the incident's report and the event's row are written together at the next sample, as the " +
                        "budget allows: the row is appended to a file that holds its first part only, while a file that holds both ends is replaced whole only by a write " +
                        "that must be made, and until then the report lists the event and counts the file's rows as they are. (An appended row sits at the end of the file, " +
                        "after rows of later times, until the next write that must be made. A write that can wait adds at most 100 rows; when more late events than that " +
                        "come at once, the rest follow 100 at a time, 30 seconds apart (as the budget allows), each time with the report again, so its line counts the " +
                        "file's rows:", design);
        Assert.Contains("That write comes two minutes after the window's " +
                        "end (`IncidentManager.LateEventMargin`: a rollup is handed over up to a minute and a half late, so by then every late event of the window can have " +
                        "come): when the file does not hold the window's events the report lists, each once and in time order (`IncidentWriter.LacksEvents`), the folder is " +
                        "written once more, a write that must be made, whatever the budget, for an incident of any kind: a file that holds both ends is replaced whole (about " +
                        "0.3 MB at the owner's mix), another gets every row left and is written in time order, and the report is written beside it, so the two agree. It is " +
                        "made once, and again only when still more of the window's events come, no sooner than two minutes after the last such write.", design);
        Assert.Contains("So every write of a closed incident that must be made (its close, a trace attached, the write for its window's late events below, the end of a run) " +
                        "leaves its file with every row it is to hold, in time order, and no row waiting:", design);
        Assert.Contains("The events after its window's end a closed incident takes when it is written, and at the latest before the buffer lets them go " +
                        "(`IncidentManager.DropEvents`): every incident PoolWatch holds in memory has looked at every event before it goes, and keeps or counts each by its " +
                        "own time against its own window.", design);
        Assert.Contains("And every incident PoolWatch writes is one it holds: none leaves memory while a trace that may be attached to it records or is read " +
                        "(`IncidentManager.Process`; they leave at the first sample after the trace).", design);
        Assert.Contains("write it when its file does not hold both ends as they are (its closing write failed, or its window took events in after it: below), so a later " +
                        "run keeps them; the incident's report is written with it then, so the two say the same of the file", design);
        Assert.Contains("An event of a closed incident's window that comes after its last write (a short command's rollup is handed over up to a minute and a half after " +
                        "its first start, so those of a trace's last seconds can) is taken as it comes: `report.md` lists it at the next sample, as the budget allows, with " +
                        "its row in a file that holds the first part only (added at the file's end, after rows of later times; at most 100 rows a write, the rest 100 at a " +
                        "time, 30 seconds apart, each time with `report.md` again), and counts the rows of a file that holds both ends as they are. Two minutes after the window's " +
                        "end, when every late event of the window can have come, PoolWatch writes the folder once more if `events.csv` does not hold the window's events as " +
                        "`report.md` lists them, each once and in time order (`IncidentManager.LateEventMargin`): a write that must be made, whatever the budget, which replaces " +
                        "a file that holds both ends whole and gives another every row left, in time order, with `report.md` beside it. It is made once; again only when still " +
                        "more of the window's events come, and then no sooner than two minutes after the last such write.", technical);
        Assert.Contains("Every write of a closed incident that must be made (its close, a trace attached, the write for its window's late events, the end of a run) leaves its " +
                        "`events.csv` with every row it is to hold, in time order.", technical);
        Assert.Contains("(and at the end of a run, or when it leaves memory, if the file does not hold them as they are: its `report.md` is written with it then)", technical);
        Assert.Contains("An event of a closed incident's window that comes after its file was last written (a short command's `Rollup` is handed over up to a minute and a " +
                        "half after its first start, so those of a trace's last seconds can) is listed in `report.md` at the next sample or soon after; a file that holds " +
                        "the first part only gets its row then too, at its end, after rows of later times. Two minutes after the window's end, when every late event of the " +
                        "window can have come, PoolWatch writes the folder once more if `events.csv` does not hold the window's events as `report.md` lists them, each once " +
                        "and in time order (a file that holds both ends is replaced whole then, its `report.md` with it; one that holds the first part only is written in " +
                        "time order), and again only when still more of them come later; until then that `report.md` counts the file's rows as they are.", guide);
        Assert.Contains("with no more than 500 it has no latest part, and its close leaves its file with every event of the window, in time order.", guide);
        // No event from after the window's end is counted as left out: the counts are taken again when the end moves, and where PoolWatch no longer holds the
        // events to do so, the texts say that the count runs past the window's end.
        Assert.Contains("So no event from after the window's end is counted as left out: each is counted by its own time against the window's end, and when the end is " +
                        "measured again after events were counted, they are counted again.", design);
        Assert.Contains("At the next write that takes both ends (the close, a trace attached, an export) the incident then takes its latest part and its counts again from " +
                        "the buffer (`IncidentManager.Refit`), as its first part follows the start: every event after the first part that the buffer holds is kept, counted " +
                        "as left out, or left as after the end, by its own time against the end as it is measured now, so the file has the latest 500 up to that end and the " +
                        "`Gap` row counts exactly the events between its two parts.", design);
        // What the buffer has let go of the events an incident had let go stays counted as it was made, those it had set aside among them: all the window's
        // while the end is after the latest event the buffer let go, so the rest is taken again all the same.
        Assert.Contains("What the incident had let go that the buffer has let go too stays counted as it was made, up to the time of the latest event the buffer let go, the " +
                        "events it had set aside among them: with the end measured after that time they are all the window's, left out between its two parts, and the latest " +
                        "part is the events after the last of them up to the end (500, or as many as came between: the row says how many the file keeps). (Before, nothing " +
                        "was taken again once the buffer had let go of any event set aside, also when every event it had let go was before the new end: the file then had " +
                        "only the events after those set aside for a latest part, or none, and its `Gap` row could count events from after the window's end as ones " +
                        "PoolWatch no longer held while it held them all.)", design);
        Assert.Contains("Only where the buffer no longer holds the events from after the new end (over 20,000 events since it, or settings that keep the events for less time " +
                        "than the history) does such a count hold events from after the end, and the `Gap` row and `report.md` then say that it runs past the window's end, " +
                        "and where the window ends (\"the count runs past the window's end, 06:23:50 when the file was written: PoolWatch no longer held those events, to " +
                        "count each by its own time against that end\" in `report.md`).", design);
        Assert.Contains("Every incident PoolWatch holds in memory looks at each event before PoolWatch lets it go, and none leaves memory while a trace that may be attached " +
                        "to it records or is read, so each event is kept or counted by its own time against the incident's own window: no event from after the window's end " +
                        "is counted as left out. When the end is measured again after events were counted (earlier than events the incident has already let go: it holds its " +
                        "latest 500 up to the end, and 500 more while it is open; or, once more than 4,000 events came after it, later, to among the earliest of those, which " +
                        "it had set aside), the incident takes its latest part and its counts again from the events PoolWatch holds, when both ends are next written " +
                        "(`IncidentManager.Refit`), as the first part follows the start; what it had let go that PoolWatch no longer holds either stays counted as it was " +
                        "made. Only where PoolWatch no longer holds the events from after the new end (over 20,000 events since it; the end is measured within the 70 " +
                        "minutes of raw history, and the events of 90 minutes are held) does such a count hold events from after the end, and the `Gap` row and `report.md` " +
                        "then say that it runs past the window's end, and where the window ends.", technical);
        Assert.Contains("The `Gap` row counts each event by its own time, so no event from after the window's end is counted as left out: when the window's end is measured " +
                        "again after events were counted, PoolWatch takes the latest part and the count again from the events it holds (those it no longer holds stay " +
                        "counted as they were). Only on a PC so busy that it no longer holds the events from after the new end (over 20,000 events since it) does such a " +
                        "count hold events from after the end; the row and `report.md` then say so, with where the window ends (\"the count runs past the window's end, ... " +
                        "when this file was written: PoolWatch no longer held those events, to count each by its own time against that end\").", guide);
        // The condition the sentence had while the counts stayed as they were made when the end moved, and what each said of a closed file then.
        foreach (var (name, text) in new[] { ("DESIGN.md", design), ("TECHNICAL.md", technical), ("AGENTS.md", guide), ("CHANGELOG.md", changelog) })
        {
            Assert.False(text.Contains("while the end stays where it was when the event was counted", StringComparison.Ordinal), name + " gives the sentence its old condition");
            Assert.False(text.Contains("by times PoolWatch no longer holds", StringComparison.Ordinal), name + " says the count stays by times PoolWatch no longer holds");
            Assert.False(text.Contains("later time is then after the window's end", StringComparison.Ordinal), name + " leaves a Gap row's later time after the window's end unsaid by the row");
            Assert.False(text.Contains("That is the only way an open file loses a row", StringComparison.Ordinal), name + " says a follow is the only way an open file loses a row");
            // What each said while nothing was taken again once the buffer had let go of an event set aside (a count then stayed as it was made
            // with fewer than 20,000 events since the new end), and while the rows that waited were said to come in one more write.
            Assert.False(text.Contains("does a count stay as it was made", StringComparison.Ordinal), name + " says a count stays as it was made only past 20,000 events since the end");
            Assert.False(text.Contains("the rest 30 seconds on", StringComparison.Ordinal), name + " says the rows that waited come in one more write");
            Assert.False(text.Contains("(30 seconds on, as the budget allows)", StringComparison.Ordinal), name + " says the rows that waited come in one more write");
        }
        // The runs that measured what this costs were made the same day as the runs they are set against (not "on another day").
        Assert.Contains("two runs of that code, four hours later the same day, measured 0.142% and 0.153%", design);
        Assert.DoesNotContain("two runs of that code, on another day", design);
        // What each said while a file with both ends got a late event only at the end of the run or as its incident left memory, and
        // while an incident that had left memory could be written once more.
        Assert.DoesNotContain("gets it at the end of the run or as the incident leaves memory", technical);
        Assert.DoesNotContain("is replaced with it when PoolWatch exits or the incident leaves", guide);
        Assert.DoesNotContain("Only an incident PoolWatch no longer holds (one that left memory and is written once more) can find", design);
        // What the design page said while a closed incident looked at the buffer only when it was written.
        Assert.DoesNotContain("When the buffer let events go before a closed incident looked at them and it re-opens", design);
        Assert.DoesNotContain("(the buffer let its events go before a closed incident looked at them)", design);
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

    /// <summary>
    /// The changelog, the guide for AI assistants and the example report say a closed incident's level with the time it
    /// was measured at, and "now" only of an open incident's (a closed incident's report is written again hours after
    /// its close, and its level is as its close measured it); the same of what its trace's answer says of that level
    /// ("was still held at 07:05:53" for "is still held"), of a suspect that had not exited ("still running when it
    /// closed" for "still running") and of a growth found still held after its cause went quiet (the "Retained" line:
    /// "still held ... at 07:20:53" for "still holds"); and the changelog says what the allocation rates' line is
    /// over (the growth, the drop, or the trace). <c>IncidentRewriteTests</c> holds the reports to it
    /// (<c>AClosedIncidentsReportWrittenAgainLaterSaysItsLevelAndItsPoolAsTheyWereAtItsClose</c>,
    /// <c>AClosedIncidentsTraceAnswerSaysTheTimeItsLevelIsOf</c>,
    /// <c>AClosedIncidentsSuspectIsSaidAsStillRunningWhenItClosedNotNow</c>,
    /// <c>TheSpanAReportCountsOverIsNamedByWhatTheIncidentIsOf</c>).
    /// </summary>
    [Fact]
    public void TheDocsSayAClosedIncidentsLevelWithItsTimeAndWhatItsAllocationRatesAreOver()
    {
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        string changelog = Doc("CHANGELOG.md"), guide = Doc("src", "PoolWatch", "Assets", "AGENTS.md"), example = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "docs", "example-incident", "report.md"));
        Assert.Contains("a report written again after its incident closed says what was so at its close, with the time: the system's figures, the tag's allocation rates, " +
                        "which pool the tag was in, and the level the tag had reached. A closed incident's report gives that level with the time it was measured (\"244 MB at " +
                        "07:05:53\") and says \"now\" only while the incident is open (in 1.0.0-beta.2 a report written again hours after the close still said \"now\" of the " +
                        "level at the close). The same goes for what its trace showed, for its suspects and for a growth found still held after its cause went quiet: a " +
                        "closed incident's report says the growth \"was still held at 07:05:53\" where an open one's says \"is still held\", a suspect that had not exited " +
                        "is \"still running when it closed\", and its \"Retained\" line gives the time it is of (\"still held 12 MB of its 12 MB rise at 07:20:53\") (in " +
                        "1.0.0-beta.2 a closed incident's report said \"is still held\", \"still running\" and \"still holds\", whenever it was written). The line with " +
                        "the allocation rates says what they are over: the growth, the drop in a sharp drop's report, or the trace in the report of a trace whose tag did " +
                        "not grow (in 1.0.0-beta.2 it called that span the growth in a drop's and a trace's report too).", changelog);
        // 1.0.0-beta.2 did not always say "during the growth": it also said "from 10:42 (the tag was too small to keep its counters before)".
        Assert.DoesNotContain("it always said \"during the growth\"", changelog);
        Assert.Contains("and `currentAtUtc`, the sample `currentBytes` is of: the newest one while the incident is open; for a closed incident the one of its close (or of a " +
                        "later check that found the growth still held), however much later the report was written, so it is not the tag's level now (`report.md` gives it as " +
                        "\"244 MB at 07:05:53\", and says \"now\" only of an open incident;", guide);
        Assert.Contains("They are as PoolWatch last ranked them: for a closed incident at its close (or as its trace was attached, after it), however much later the report " +
                        "was written. So `report.md` says \"still running\" only of an open incident's suspect; of a closed incident's suspect that PoolWatch knew to be " +
                        "running it says \"still running when it closed\", which is not whether it runs now (a folder an older PoolWatch wrote says \"still running\" of a " +
                        "closed incident's too);", guide);
        Assert.Contains("`traceAnswer` also says where the tag's level was against its growth: of an open incident at the newest sample (\"and is still held\", \"has been " +
                        "freed since\", \"it is now empty\"), of a closed one at the sample `currentAtUtc` gives, with its time (\"and was still held at 07:05:53\", \"had been " +
                        "freed by 07:05:53\", \"it was empty at 07:05:53\"; a folder an older PoolWatch wrote says \"is still held\" of a closed incident too, which is of its " +
                        "close or of when the growth was found still held);", guide);
        Assert.Contains("`retention` (when a closed incident's growth was found still held after its likeliest cause went quiet or exited: `timeUtc`, `activityStoppedUtc`, " +
                        "`retainedBytes`, and `note`, which says it with that time, \"still held 12 MB of its 12 MB rise at 07:20:53\", as `report.md`'s \"Retained\" line " +
                        "does; a folder an older PoolWatch wrote says \"still holds\", which is of `timeUtc` too, not of now);", guide);
        // The example report is a closed incident's: its Growth line gives the level with its time, its "Retained" line the time the growth was found still
        // held (the same sample: its close, 30 s after its suspect went quiet), and its suspects are still running when it closed, as PoolWatch writes them now.
        Assert.Contains("- Growth: 6.6 MB -> 18.7 MB (+12.1 MB) from 03:46:04 to 03:46:44; 18.7 MB at 03:47:14", example);
        Assert.Contains("closed 2026-10-26 03:47:14 +00:00", example);
        Assert.DoesNotContain("; now ", example);
        Assert.Contains("- **Retained**: File still held 12.1 MB of its 12.1 MB rise at 03:47:14, 30 s after Leaker.exe (PID 7716) went quiet.", example);
        Assert.DoesNotContain("still holds", example);
        var started = example.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("- Started: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, started.Count);
        Assert.All(started, l => Assert.Contains("; still running when it closed", l));
        Assert.Equal("- Started: 2026-10-26 03:41:05 +00:00; still running when it closed; orphaned 2026-10-26 03:45:54 +00:00", started[3]);
    }

    /// <summary>
    /// What the docs say of this release's report texts and of a write that fails. The guide for AI assistants says
    /// that the clock times <c>report.md</c> gives, and those inside <c>report.json</c>'s texts, are the PC's local
    /// time (the fields named <c>...Utc</c> beside them are UTC: an assistant that matched a <c>note</c>'s "at
    /// 07:05:20" to its <c>timeUtc</c> was out by the PC's offset). The changelog says what a sharp drop's plain words,
    /// a closed incident's, a likely cause's line and a non-paged-pool report's title say (the limit the pool went
    /// over), what a trace's report says until its trace has finished (and a vendor bundle's list of incidents with
    /// it), what a draft bug report lists as attached, and that a closed incident's report that
    /// could not be written is tried again, the rest of it written beside a held <c>timeline.csv</c> or <c>processes.csv</c>,
    /// and its chart and its two other CSV files, once PoolWatch no longer holds the incident's first samples, only
    /// by a try that has at least as many of them as the file; the design and technical pages say when, which
    /// tries are for the chart files alone, and what the log says of each.
    /// <c>IncidentRewriteTests</c> holds the reports and the tries to it (<c>ADropsPlainWordsSayTheDropOverItsSpan</c>,
    /// <c>AClosedIncidentWhoseTagDidNotGrowIsSaidInThePastAndOneWhoseTagFellSaysSo</c>,
    /// <c>ThePlainWordsSayWhatTheTagDidOverTheIncidentsSpanInThePresentOnlyWhileItIsOpen</c>,
    /// <c>AClosedIncidentsOwnerLineSaysWhatItsHandlesDidOverTheWindowNotWhatItHoldsNow</c>,
    /// <c>AnIncidentForTheWholeNonPagedPoolSaysAndRisingOnlyWhileItIsOpen</c>,
    /// <c>ATracesReportSaysThatItsTraceHadNotFinishedUntilTheTraceIsAttached</c>,
    /// <c>ATracesReportWhoseTraceRecordedNothingSaysThatNotWhatTheTagDidOverNoTime</c>,
    /// <c>AClosedIncidentsFolderThatCouldNotBeWrittenIsTriedAgainAMinuteOnAndSaidOnce</c>,
    /// <c>AClosedIncidentsReportIsWrittenWhileAnotherProgramHoldsItsTimelineCsvOrItsProcessesCsv</c>,
    /// <c>AnOpenIncidentsReportIsWrittenWhileAnotherProgramHoldsItsTimelineCsv</c>,
    /// <c>ATryMadeAfterTheHistoryMovedPastAClosedIncidentLeavesItsChartAndItsTwoCsvsAsTheyAre</c>,
    /// <c>AFileThatStopsBeforeALongIncidentsCloseIsReplacedByATryThatReachesTheClose</c>,
    /// <c>AFileLeftAtOneCloseIsNotSaidLeftAtALaterCloseWhoseFolderIsWritten</c>,
    /// <c>ATryThatHoldsAsManyOfTheWindowsSamplesAsTheFileReplacesItAndOneThatHoldsOneFewerLeavesIt</c>,
    /// <c>AFolderThatNeverHadAChartIsNotSaidWrittenAgainButForItsChart</c>,
    /// <c>ATraceAttachedAfterTheCloseHasProcessesCsvMadeAgainWithItsRanking</c>,
    /// <c>AReopenedIncidentsFolderIsSaidWrittenAgainOnlyOnceItsChartAndCsvsAreWritten</c>,
    /// <c>ATryForAHeldTimelineCsvOrProcessesCsvWritesNoReportAndAnOpenIncidentIsRefreshedAsWithoutTheHold</c>,
    /// <c>ATryThatMakesAFileTheReportDoesNotShowWritesTheReportAgain</c>,
    /// <c>ATryStillWritesTheWholeFolderWhenAWriteThatCouldWaitWroteAllButTimelineCsvAfterTheWholeFolderHadFailed</c>,
    /// <c>ATryAfterAWriteThatFailedAtTheReportWritesTheWholeFolderThoughAnEarlierWriteFailedAtTimelineCsvAlone</c>,
    /// <c>ATryForAnIncidentThatChangedSinceItsReportWasWrittenWritesTheWholeFolder</c>,
    /// <c>ALateEventThatComesJustBeforeATryForTheChartFilesAloneIsStillWrittenWithItsReport</c>,
    /// <c>ADraftBugReportListsAsAttachedTheFilesItsFolderHoldsWhenTheReportIsWritten</c>,
    /// <c>ADraftBugReportDoesNotListAnEventsCsvItsFolderDoesNotHold</c>,
    /// <c>AnOpenIncidentWhoseClosingWriteFailsAtTheEndOfARunIsTriedOnceAndLoggedOnce</c>); the bundle's list is held by
    /// <c>RecommendationInputsBuilderTests.ATracesIncidentWithNoSpanIsListedInTheBundleByWhatItsReportSays</c>.
    /// </summary>
    [Fact]
    public void TheDocsSayWhatTimeAReportsTextsAreInWhatItsPlainWordsSayAndThatAFailedWriteIsTriedAgain()
    {
        static string Doc(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])));
        string design = Doc("docs", "DESIGN.md"), technical = Doc("docs", "TECHNICAL.md"), guide = Doc("src", "PoolWatch", "Assets", "AGENTS.md"), changelog = Doc("CHANGELOG.md");
        // The times inside the texts are local.
        Assert.Contains("Times inside CSV and JSON files are UTC (`2026-10-28T09:12:00.000Z`); `poolwatch.log` and the dates and times in folder names and incident ids " +
                        "are local time, so match an id to CSV rows through the time zone. The clock times `report.md` gives are the PC's local time too, and so are those " +
                        "inside these texts of `report.json`: `headline`, `plainAnswer`, `traceAnswer`, `retention`'s `note`, the `timeline` texts and the suspects' " +
                        "`evidence` lines. They are in the time zone `report.md`'s stamps show (\"opened 2026-10-28 10:12:00 +01:00\"), so on that PC a `timeUtc` of " +
                        "`2026-10-28T09:12:00Z` stands beside a `note` that says \"at 10:12:00\".", guide);
        // As PoolWatch writes them: a clock time in a text is the local time of day, and report.md's stamps carry the offset (a summer
        // time: on a PC whose zone keeps summer time, or is not UTC, the two differ from the UTC time of day).
        var utc = new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc);
        Assert.Equal(utc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), PoolWatch.Core.Detection.Fmt.Clock(utc));
        Assert.Equal(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture), PoolWatch.Core.Detection.Fmt.Stamp(utc));
        // What the plain words, a likely cause's line and the title say, and a closed incident's report that could not be written.
        Assert.Contains("`headline` (one line) and `plainAnswer` (the answer in plain words, with the suspects grouped by who they work for: an app, a service, a Claude " +
                        "Code session). Of a closed incident they say \"was flat over 1 min\", \"No clear cause was found\" and \"Non-paged pool rose over 5.0 GB\", where " +
                        "an open one's say \"is flat over ...\", \"No clear cause yet\" and \"Non-paged pool over 5.0 GB and rising\", and a drop's `plainAnswer` says the " +
                        "drop (\"fell 140 MB in 1 min\"). The figure in a `NonPagedTotal` headline is the limit the pool was over when the alert fired: not the pool's size " +
                        "(the alert's `rule` under `alerts` gives it) and not that alert's `bytes`, which is how much the fastest-growing tag rose in 10 minutes. A " +
                        "`Capture` incident written before its trace has finished has no span yet: its `headline` says so, with the time of the newest sample when it " +
                        "was written (\"its trace had not finished by 07:05:20\"), as `report.md`'s title and Growth line do, and its `plainAnswer` is empty or says the " +
                        "same; its `growthBytes` of 0 is then no finding of a trace. One whose trace recorded nothing PoolWatch could use never gets a span: its " +
                        "`headline` says that instead (\"its trace recorded nothing PoolWatch could use\"), and `capture` has the trace's `error` or `notes`. A folder an " +
                        "older PoolWatch wrote gives a growth in both cases (\"grew ... in ...\"), which is of no trace, says \"is flat\", \"No clear cause yet\" and " +
                        "\"and rising\" of a closed incident too, gives the alert's `bytes` as the figure in a `NonPagedTotal` headline, and in a drop's `plainAnswer` says " +
                        "\"is flat\" or \"grew\", which is of the time just before the drop, not of the drop;", guide);
        // A trace's report before its trace has finished, and one whose trace recorded nothing.
        Assert.Contains("Until the trace has finished, that report gives no growth: its title and its Growth line say \"its trace had not finished by 07:05:20\" (the " +
                        "time of PoolWatch's latest measurement when the report was written), and when a trace recorded nothing PoolWatch could use, they say that.", changelog);
        // The time in it is the newest sample's at that write (IncidentWriter.NoTraceSpan), which the report is written at or after: not a time it was written "by".
        Assert.DoesNotContain("written by)", changelog);
        Assert.DoesNotContain("the time it was written by", guide);
        Assert.Contains("Until its trace is attached the incident has no span: its report's title, its Growth line, its plain words (when it has a suspect) and its draft " +
                        "bug report say that its trace had not finished by the time of the newest sample at that write, not what the tag did over no time (before: \"grew " +
                        "0 B in 0 s; no suspect stood out\"), and those of an incident whose trace recorded nothing PoolWatch could use say that " +
                        "(`IncidentWriter.NoTraceSpan`).", design);
        // The vendor bundle's incidents.md lists such a trace by the same words, and a draft bug report lists the files its folder holds.
        Assert.Contains("and when a trace recorded nothing PoolWatch could use, they say that. A vendor bundle's `incidents.md` lists such a trace in the same words, not " +
                        "as a growth (the time it gives for a trace that had not finished is that of PoolWatch's latest measurement when the bundle was made).", changelog);
        Assert.Contains("The vendor bundle's `incidents.md` lists such an incident by the same words (its trace had not finished by the time of the newest sample as the " +
                        "bundle is made, or recorded nothing PoolWatch could use), and gives no growth for it (`BundleBuilder.IncidentLine`; before: \"100 MB -> 100 MB " +
                        "(+0 B)\").", design);
        Assert.Contains("The draft bug report at the end of `report.md` lists as attached evidence only the files the folder held when that report was written (in " +
                        "1.0.0-beta.2 it always listed `timeline.csv`, `processes.csv` and `events.csv`).", changelog);
        Assert.Contains("A sharp drop's report says the drop in plain words (\"fell 140 MB in 1 min\"; in 1.0.0-beta.2 it said the tag \"is flat\" or \"grew\"). In a closed " +
                        "incident's report a tag that did not move \"was flat\", and one that only fell \"fell\" by how much (in 1.0.0-beta.2 both were \"is flat\", whenever " +
                        "the report was written), and where it names no cause it says \"No clear cause was found\" (an open incident's report says \"No clear cause yet\", " +
                        "as 1.0.0-beta.2 said of a closed one too). A likely cause's line says what its handles and its activity did, not what they do now: \"its File " +
                        "handles rose by 24,000\" and \"rose and fell with the tag: strongly\", in a report and in the window's details of a growing tag (in 1.0.0-beta.2 " +
                        "\"holds 24,000 more File handles\" and \"rises and falls with the tag\", which a report written after the program had exited still said). The " +
                        "title of a report for the whole non-paged pool names the limit the pool went over (5.0 GB unless you set another): \"Non-paged pool over 5.0 GB " +
                        "and rising\" while the incident is open, and \"Non-paged pool rose over 5.0 GB\" once it has closed (in 1.0.0-beta.2 the figure was how much the " +
                        "fastest-growing tag had risen in 10 minutes, so the title could read \"over 100 MB and rising\" of a pool at 5 GB, and it said \"and rising\" after " +
                        "the incident had closed too). When a closed incident's report cannot be written because another program holds one of its files, then for as long " +
                        "as PoolWatch still holds the incident it keeps trying, about once a minute, and tries once more as it stops; its log says so once, and once more " +
                        "when the files are written again. While it is `timeline.csv` or `processes.csv` that is held, the rest of the report is written all the same, so " +
                        "`report.md` says that the incident closed, and when. The chart, `timeline.csv` and `processes.csv` are made from the measurements PoolWatch holds " +
                        "of the incident's time; it keeps the last 70 minutes of measurements, and lets the oldest go in batches. Once it no longer holds the incident's " +
                        "first ones (for an incident that ran about 50 minutes or longer, that is within minutes of its close), a try writes a file that could not be " +
                        "written before only when it has at least as many of the incident's measurements as the file holds already, so a file that stops before the " +
                        "incident closed is replaced by one that runs to the close; when a try would have fewer, it leaves the file as it is, the log names the file, " +
                        "and no further try is made for it (in 1.0.0-beta.2 the report stayed as it was until something about the incident changed, which for a closed " +
                        "incident could be never).", changelog);
        // No text gives the 70 minutes as how long a held file is still made for: a try decides by the measurements it has against those the file holds,
        // and for an incident whose window starts at the oldest sample PoolWatch holds that begins within minutes of its close.
        Assert.DoesNotContain("stays held for longer", changelog);
        Assert.DoesNotContain("(it keeps 70 minutes of them)", technical);
        Assert.Contains("When a write of a closed incident's folder fails (another program holds one of its files open), the folder is tried again, as a write that " +
                        "must be made: at the first sample a minute or more after the write failed, and a minute after each try that fails, while PoolWatch holds the " +
                        "incident (`IncidentManager.FailedWriteRetry`), and once more at the end of a run. A write that cannot write `timeline.csv` or `processes.csv` " +
                        "does not stop there: it writes the other of the two, `events.csv` and the two reports, and counts as failed for that file " +
                        "(`IncidentWriter.ChartFailure`), so while another program holds its `timeline.csv`, `report.md` still says that the incident closed. (Before, " +
                        "the write stopped at the first file it could not write: `report.md` could go on saying \"still open\" of a closed incident while the file was " +
                        "held.) A try writes the whole folder, unless all that is still to write is `timeline.csv` or `processes.csv` (the write that failed wrote the " +
                        "reports, and no earlier failed write left more: `Incident.ChartsAloneFailed`) and no change waits to have the folder written (`Incident.Dirty`, " +
                        "set by what has a closed incident's folder written again: a new line on its timeline, a growth found still held, a suspect's session found; a " +
                        "repeat counted on a line and a late event have their own writes, as without a failed write): such a try writes the chart and those two files " +
                        "alone, and `events.csv` and the two reports only when it made a file the report does not show yet (its chart line, the files its draft bug " +
                        "report lists as attached), so the report stays true (`IncidentManager.Retry`). (Before, every try wrote both reports again: they give " +
                        "PoolWatch's own cost as it is at each write, so a report is hardly ever the same as the one before. While another program held a closed " +
                        "incident's `timeline.csv` that came to a report a minute, about 5.4 MB an hour at a report of about 90 KB, more than the whole 5 MB an hour of " +
                        "section 3; and as a write that must be made spends the report folders' budget too, the open incidents' refreshes waited for as long as the " +
                        "file was held.) The try at the end of a run is of the whole folder. A try makes the chart, `timeline.csv` and `processes.csv` from the " +
                        "window's samples while the raw history (70 minutes) still reaches back to the start of the incident's window. Once it does " +
                        "not, the chart, `timeline.csv` and `processes.csv` would come out of fewer of the window's samples, or of samples from after the incident, and " +
                        "each of the three is decided on its own (`IncidentWriter.Leaves`): one that was written for the incident's close stays as that write made it, " +
                        "and one that never was (another program held it, the folder could not be written, or the chart could not be drawn) is made only when the try " +
                        "holds at least as many of the window's samples as the file holds already, and at least one. So such a try replaces a file that stops before " +
                        "the close with one that reaches it, and no file comes out of fewer of the window's samples than it holds. (The window of an incident that ran " +
                        "about 50 minutes or longer starts at the history's oldest sample, and the history lets its oldest samples go 32 at a time, every 5 minutes 20 " +
                        "seconds at a sample every 10 seconds: it stops reaching back to that window's start at most that long after the close, not 70 minutes after " +
                        "it. A rule that left such a file whenever the history no longer held the window's first sample could leave a `timeline.csv` that stopped 20 " +
                        "minutes before the close for good, after another program had held it for 30 seconds. The place of the window's first sample is kept from the " +
                        "closing write on, also when that write is refused its folder (`FileState.WindowStart`), so the samples a file holds from before the window are " +
                        "not counted against a try.) A try that leaves a file that was never written for the close makes its failure good all the " +
                        "same: no further try is made for that file (`IncidentWriter.ChartFilesLeft`; a later write, for a note or an alert counted on the timeline, " +
                        "leaves it too). A write of its report and rows that is made meanwhile (a late event's) does not stand for a try: the try is still made. " +
                        "The log says once that the write failed and that PoolWatch tries again, however many tries fail after it, and once what came of it: that the " +
                        "folder is written again, or, of a try that left such a file, that the folder is written again but for that file, which stays as it is, with " +
                        "why (`IncidentManager.FolderWrittenBut`; a chart is named so only when the folder has one, as a chart that could not be drawn is no failed " +
                        "write and a folder without one is whole; a write that first fails at the end of a run, or as its incident leaves memory, is only said to have " +
                        "failed: nothing tries it again; an incident still open at the end of a run is closed and written once there, whether or not that write is " +
                        "made). An incident that re-opens before its folder is written gets its report at once, as every re-opening does, and the log says that its " +
                        "folder is written again only once a write with the chart and the CSVs is made: a refresh with them, its close, or a try after that close.", design);
        Assert.Contains("An open incident's write that fails waits for its next change, as before; when it is its `timeline.csv` or its `processes.csv` that cannot be " +
                        "written, its report is written all the same, and the file is tried again at its next refresh with the chart.", design);
        Assert.Contains("When a write of a closed incident's folder fails (another program holds one of its files open), PoolWatch tries the folder again a minute or " +
                        "more after that, and a minute after each try that fails, while it holds the incident, and once more when it stops. A write that cannot write " +
                        "`timeline.csv` or `processes.csv` still writes every other file, so `report.md` and `report.json` are not behind for one of those two. The " +
                        "tries that follow such a write are for the chart, `timeline.csv` and `processes.csv` alone, and write the two reports again only when they " +
                        "made a file the report does not show yet (its chart line, the files its draft bug report lists as attached), so a try that can make none of " +
                        "the three writes nothing; a try writes the whole folder instead while more than those files is still to write (a write failed at another " +
                        "file), while a change that has the folder written again waits (a new line on the incident's timeline that the write budget held back, say), " +
                        "and when PoolWatch stops. A try makes those three files from the window's samples while PoolWatch still holds the samples from the start of " +
                        "the incident's window (it keeps the last 70 minutes of " +
                        "samples and lets the oldest go 32 at a time; the window of an incident that ran about 50 minutes or longer starts at the oldest of them, so " +
                        "for such an incident that is at most 5 minutes 20 seconds after its close, at a sample every 10 seconds). After " +
                        "that, of `timeline.png`, `timeline.csv` and `processes.csv`, one that was written for the incident's close stays as it is, and one that never " +
                        "was is written only when the try holds at least as many of the window's samples as the file holds already (so a file that stops before the " +
                        "close is replaced by one that reaches it); a try that would hold fewer leaves the file, and no further try is made for it. `poolwatch.log` " +
                        "says once that the write failed, and once what came of it: that the folder is written again, or that it is written again but for the file " +
                        "that stays as it is (of an incident that re-opened meanwhile, once its chart and CSV files are written again).", technical);
    }
}
