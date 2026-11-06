using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

public class CopyTests
{
    [Fact]
    public void TsvHasHeaderAndEscapesCells()
    {
        string t = CopyText.Tsv(["Process", "PID", "Command line"], [["node.exe", "12", "node\tx.js\r\nmore"], ["git.exe", "13", "git status"]]);
        Assert.Equal("Process\tPID\tCommand line\r\nnode.exe\t12\tnode x.js  more\r\ngit.exe\t13\tgit status\r\n", t);
    }

    [Fact]
    public void RedactionAppliesToCopies()
    {
        var r = new RedactionInfo("alice", "ALICE-PC", [@"C:\Users\alice"]);
        Assert.Equal(@"<HOME>\x on <MACHINE>", CopyText.Apply(@"C:\Users\alice\x on ALICE-PC", r));
        Assert.Equal("as is", CopyText.Apply("as is", null));
    }

    [Fact]
    public void IncidentSummaryFromReportJson()
    {
        string json = """
        { "id": "20261026-0530-File", "kind": "Growth", "tag": "File", "headline": "`File` grew 12 MB; top suspect Leaker.exe",
          "plainAnswer": "File objects (File, non-paged) grew 12 MB in 39 s. Most likely cause: Leaker (strong).",
          "window": { "fromUtc": "2026-10-26T05:29:00Z", "onsetUtc": "2026-10-26T05:30:00Z", "toUtc": "2026-10-26T05:31:00Z" },
          "suspects": [ { "displayName": "Leaker.exe", "pid": 7716, "confidence": "StronglyCorrelated", "commandLine": "Leaker.exe leak-handles",
                          "evidence": [ "direct charge: File handles 0 -> 30,026" ] } ] }
        """;
        string s = CopyText.IncidentSummary(json);
        Assert.StartsWith("`File` grew 12 MB; top suspect Leaker.exe\r\nIncident 20261026-0530-File (Growth), tag File\r\n", s);
        Assert.Contains("Most likely cause: Leaker (strong).", s);
        Assert.Contains("1. Leaker.exe (PID 7716), strong evidence\r\n   - direct charge: File handles 0 -> 30,026\r\n   command line: Leaker.exe leak-handles", s);
    }

    /// <summary>An incident's report.json as IncidentWriter writes it: the tag in a code span in the headline, the window in UTC to the tick.</summary>
    private const string Ntfc = """
        { "id": "20261031-191052-Ntfc-6366744e", "kind": "Growth", "tag": "Ntfc",
          "headline": "`Ntfc` (ntfs.sys CCB_DATA) grew 4 MB in 29 s; top suspect Leaker.exe PID 1512, started 19:10:26 (strongly correlated)",
          "plainAnswer": "NTFS file records (Ntfc, paged) grew 4 MB in 29 s. Most likely cause: Leaker (strong).",
          "window": { "fromUtc": "2026-10-31T19:10:12.7734278Z", "onsetUtc": "2026-10-31T19:10:22.7705869Z", "toUtc": "2026-10-31T19:10:52.7693026Z" },
          "suspects": [ { "displayName": "Leaker.exe", "pid": 1512, "confidence": "StronglyCorrelated", "commandLine": "Leaker.exe leak-files",
                          "evidence": [ "direct charge: File handles 0 -> 4,026", "lifecycle: started 19:10:26, 4 s after growth onset" ] } ] }
        """;

    private static DateTime Utc(int day, int hour, int minute, int second, long ticks = 0) =>
        new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Utc).AddTicks(ticks);

    /// <summary>
    /// The Pool causes details show an incident without Markdown marks, with its kind in words and its window in local
    /// time to the second, as the window prints times (Fmt.Stamp, the onset Fmt.Clock on the same day): not
    /// "`Ntfc` (ntfs.sys ...)", "(Growth)" and "Window: 2026-10-31T19:10:12.7734278Z to ...". Everything else, the
    /// suspects' confidence in words included, reads as Copy summary has it.
    /// </summary>
    [Fact]
    public void TheDetailsShowAnIncidentWithoutMarkdownAndInLocalTime()
    {
        string pane = CopyText.IncidentDetails(Ntfc);
        Assert.StartsWith("Ntfc (ntfs.sys CCB_DATA) grew 4 MB in 29 s; top suspect Leaker.exe PID 1512, started 19:10:26 (strongly correlated)\r\n" +
                          "Incident 20261031-191052-Ntfc-6366744e (growth), tag Ntfc\r\n", pane);
        string window = $"Window: {Fmt.Stamp(Utc(31, 19, 10, 12, 7734278))} to {Fmt.Stamp(Utc(31, 19, 10, 52, 7693026))} (onset {Fmt.Clock(Utc(31, 19, 10, 22, 7705869))})";
        Assert.Contains("\r\n" + window + "\r\n", pane);
        Assert.Matches(@"\r\nWindow: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d [+-]\d\d:\d\d to \d{4}-\d\d-\d\d \d\d:\d\d:\d\d [+-]\d\d:\d\d \(onset \d\d:\d\d:\d\d\)\r\n", pane);
        Assert.Contains("\r\n1. Leaker.exe (PID 1512), strong evidence\r\n", pane);
        Assert.DoesNotContain("`", pane);
        Assert.DoesNotContain("T19:10", pane);
        Assert.DoesNotContain(".77", pane);
        // Only the headline, the kind and the window differ from what Copy summary copies.
        string[] copy = CopyText.IncidentSummary(Ntfc).Split("\r\n"), shown = pane.Split("\r\n");
        Assert.Equal(copy.Length, shown.Length);
        Assert.Equal("1. Leaker.exe (PID 1512), strong evidence", shown[5]);
        for (int i = 0; i < copy.Length; i++)
            if (i is not (0 or 1 or 2)) Assert.Equal(copy[i], shown[i]);
    }

    /// <summary>
    /// A time zone east or west of UTC with no daylight saving, passed to the details: this test process's zone is not
    /// changed. The tests can run where local time and UTC read the same, so only another zone shows which of the two the
    /// details print and compare (<see cref="PcZoneCopyTests"/> sets this test process's zone for the window's own details).
    /// </summary>
    private static TimeZoneInfo Zone(int minutes) => PcZone.Fixed(minutes);

    /// <summary>The whole of an incident's details in a zone east of UTC, where its window is on the next day.</summary>
    [Fact]
    public void TheDetailsShowTheWholeIncidentInTheirZone()
    {
        Assert.Equal("Ntfc (ntfs.sys CCB_DATA) grew 4 MB in 29 s; top suspect Leaker.exe PID 1512, started 19:10:26 (strongly correlated)\r\n" +
                     "Incident 20261031-191052-Ntfc-6366744e (growth), tag Ntfc\r\n" +
                     "Window: 2026-11-01 00:40:12 +05:30 to 2026-11-01 00:40:52 +05:30 (onset 00:40:22)\r\n" +
                     "NTFS file records (Ntfc, paged) grew 4 MB in 29 s. Most likely cause: Leaker (strong).\r\n" +
                     "Suspects:\r\n" +
                     "1. Leaker.exe (PID 1512), strong evidence\r\n" +
                     "   - direct charge: File handles 0 -> 4,026\r\n" +
                     "   - lifecycle: started 19:10:26, 4 s after growth onset\r\n" +
                     "   command line: Leaker.exe leak-files\r\n", CopyText.IncidentDetails(Ntfc, Zone(330)));
    }

    /// <summary>
    /// The window's times in the details' zone, east (+05:30) and west (-07:00) of UTC, and the onset dated by its day
    /// there: a time of day on the window's first day, with its date on another, whatever day UTC or the window's end has.
    /// A time without an offset is UTC, as the field's name says, not local time; one with an offset is that instant.
    /// </summary>
    [Theory]
    // East: the window crosses local midnight and the onset is on its second day there; in UTC all three are on the 31st.
    [InlineData(330, "2026-10-31T18:25:12.7734278Z", "2026-10-31T18:35:22.7705869Z", "2026-10-31T18:40:52.7693026Z",
                "2026-10-31 23:55:12 +05:30 to 2026-11-01 00:10:52 +05:30 (onset 2026-11-01 00:05:22 +05:30)")]
    // East and west: the window crosses local midnight with the onset on its first day.
    [InlineData(330, "2026-10-31T18:25:00Z", "2026-10-31T18:28:00Z", "2026-10-31T18:40:00Z",
                "2026-10-31 23:55:00 +05:30 to 2026-11-01 00:10:00 +05:30 (onset 23:58:00)")]
    [InlineData(-420, "2026-11-01T06:50:00Z", "2026-11-01T06:55:30Z", "2026-11-01T07:10:00Z",
                "2026-10-31 23:50:00 -07:00 to 2026-11-01 00:10:00 -07:00 (onset 23:55:30)")]
    // West: UTC crosses midnight and local time does not, so the window is on one day there.
    [InlineData(-420, "2026-10-31T23:50:00Z", "2026-11-01T00:05:00Z", "2026-11-01T00:20:00Z",
                "2026-10-31 16:50:00 -07:00 to 2026-10-31 17:20:00 -07:00 (onset 17:05:00)")]
    // Without an offset: UTC. With one (+02:00, -07:00): that instant.
    [InlineData(330, "2026-10-31T18:25:12", "2026-10-31T20:35:22+02:00", "2026-10-31T11:40:52-07:00",
                "2026-10-31 23:55:12 +05:30 to 2026-11-01 00:10:52 +05:30 (onset 2026-11-01 00:05:22 +05:30)")]
    [InlineData(-420, "2026-10-31T18:25:12", "2026-10-31T18:35:22", "2026-10-31T18:40:52Z",
                "2026-10-31 11:25:12 -07:00 to 2026-10-31 11:40:52 -07:00 (onset 11:35:22)")]
    // The first and last times there are: shown, not an error.
    [InlineData(-420, "0001-01-01T00:00:00Z", "not a time", "9999-12-31T23:59:59Z",
                "0001-01-01 00:00:00 +00:00 to 9999-12-31 16:59:59 -07:00 (onset not a time)")]
    public void TheDetailsPrintTheWindowInTheirZoneAndDateTheOnsetByItsDayThere(int minutes, string from, string onset, string to, string window)
    {
        string json = $$"""{ "headline": "h", "window": { "fromUtc": "{{from}}", "onsetUtc": "{{onset}}", "toUtc": "{{to}}" } }""";
        Assert.Equal($"h\r\nWindow: {window}\r\n", CopyText.IncidentDetails(json, Zone(minutes)));
    }

    /// <summary>
    /// The details name an incident's kind and each suspect's confidence in the words the window uses for them (the Pool
    /// causes list's "strong evidence"; "non-paged pool over the limit", as the row above the details and the tray's notice
    /// say it, not Report an issue's "over its limit"), where report.json has the names in the code. Text that names
    /// neither is shown as written, not guessed at. Copy summary keeps report.json's kind and names the confidence as the
    /// details do.
    /// </summary>
    [Theory]
    [InlineData("Growth", "growth", "StronglyCorrelated", "strong evidence")]
    [InlineData("NonPagedTotal", "non-paged pool over the limit", "ConfirmedByTrace", "confirmed by a trace")]
    [InlineData("Release", "release", "Correlated", "some evidence")]
    [InlineData("Capture", "capture", "Weak", "weak evidence")]
    [InlineData("Later", "Later", "2", "2")]
    [InlineData("NONPAGEDTOTAL", "NONPAGEDTOTAL", "weak", "weak")]
    [InlineData("", "", " Weak", " Weak")]
    public void TheDetailsNameTheKindAndTheConfidenceInTheWindowsWords(string kind, string kindWords, string confidence, string confidenceWords)
    {
        string json = $$"""
            { "id": "x", "kind": "{{kind}}", "tag": "Mm", "headline": "h",
              "suspects": [ { "displayName": "a.exe", "pid": 7, "confidence": "{{confidence}}" }, { "displayName": "b.exe", "pid": 8, "confidence": "Weak" } ] }
            """;
        Assert.Equal($"h\r\nIncident x ({kindWords}), tag Mm\r\nSuspects:\r\n1. a.exe (PID 7), {confidenceWords}\r\n2. b.exe (PID 8), weak evidence\r\n",
            CopyText.IncidentDetails(json, Zone(330)));
        Assert.Equal($"h\r\nIncident x ({kind}), tag Mm\r\nSuspects:\r\n1. a.exe (PID 7), {confidenceWords}\r\n2. b.exe (PID 8), weak evidence\r\n", CopyText.IncidentSummary(json));
    }

    /// <summary>
    /// The headline's only Markdown mark is the code span IncidentWriter.Headline writes the tag in (report.json has no
    /// other: names, evidence and command lines are as they are). So only that one goes, wherever the headline has it,
    /// for a tag with trailing spaces too; a backtick anywhere else is the program's or the user's and stays.
    /// </summary>
    [Fact]
    public void TheDetailsTakeOutOnlyTheCodeSpanTheHeadlineWritesTheTagIn()
    {
        string json = """
            { "id": "x", "kind": "NonPagedTotal", "tag": "Mm  ",
              "headline": "Non-paged pool over 2 GB and rising; fastest-growing tag `Mm` (nt!mm Memory Manager) grew 4 MB in 29 s; top suspect `Mm`.exe PID 7 (correlated)",
              "suspects": [ { "displayName": "`Mm`.exe", "pid": 7, "confidence": "Correlated", "commandLine": "pwsh -c Write-Host `\"`Mm`\"; dir **/*.cs [a](b)",
                              "evidence": [ "activity: `Mm` 4 ops" ] } ] }
            """;
        string pane = CopyText.IncidentDetails(json);
        Assert.StartsWith("Non-paged pool over 2 GB and rising; fastest-growing tag Mm (nt!mm Memory Manager) grew 4 MB in 29 s; top suspect `Mm`.exe PID 7 (correlated)\r\n", pane);
        Assert.Contains("\r\n1. `Mm`.exe (PID 7), some evidence\r\n   - activity: `Mm` 4 ops\r\n   command line: pwsh -c Write-Host `\"`Mm`\"; dir **/*.cs [a](b)\r\n", pane);
        // A headline without the tag's code span, or a report without its tag, is shown as it is.
        Assert.StartsWith("Mm (nt!mm) grew 4 MB; top suspect `odd`.exe\r\n", CopyText.IncidentDetails("""{ "tag": "Mm", "headline": "Mm (nt!mm) grew 4 MB; top suspect `odd`.exe" }"""));
        Assert.StartsWith("`Release` dropped 4 MB at 19:10:26\r\n", CopyText.IncidentDetails("""{ "headline": "`Release` dropped 4 MB at 19:10:26" }"""));
    }

    /// <summary>
    /// The window's times in the details: the onset with its date when it is not on the window's first day; a time without
    /// an offset is UTC (the field's name says so), one with an offset is that instant; one that is not a time stays as written.
    /// </summary>
    [Fact]
    public void TheDetailsWindowDatesAnOnsetOnAnotherDayAndKeepsWhatIsNotATime()
    {
        string json = """
            { "headline": "h", "window": { "fromUtc": "2026-10-26T05:29:00", "onsetUtc": "2026-10-27T07:30:00+02:00", "toUtc": "not a time" } }
            """;
        Assert.Contains($"\r\nWindow: {Fmt.Stamp(Utc(26, 5, 29, 0))} to not a time (onset {Fmt.Stamp(Utc(27, 5, 30, 0))})\r\n", CopyText.IncidentDetails(json));
        Assert.Contains("\r\nWindow:  to  (onset )\r\n", CopyText.IncidentDetails("""{ "headline": "h", "window": { } }"""));
    }

    /// <summary>
    /// Copy summary (and the tray's Copy latest incident summary) copies the summary as report.json has it: the headline's
    /// code span, which renders where it is pasted (an issue, a chat), the kind's name, and the window in UTC to the tick,
    /// which reads the same in every time zone and matches report.json. A suspect's confidence alone is in the Confidence
    /// column's words, as everywhere a person reads one, not report.json's name.
    /// </summary>
    [Fact]
    public void CopySummaryKeepsTheReportsMarkdownAndUtcTimes()
    {
        string copy = CopyText.IncidentSummary(Ntfc);
        Assert.StartsWith("`Ntfc` (ntfs.sys CCB_DATA) grew 4 MB in 29 s;", copy);
        Assert.Contains("\r\nIncident 20261031-191052-Ntfc-6366744e (Growth), tag Ntfc\r\n", copy);
        Assert.Contains("\r\nWindow: 2026-10-31T19:10:12.7734278Z to 2026-10-31T19:10:52.7693026Z (onset 2026-10-31T19:10:22.7705869Z)\r\n", copy);
        Assert.Contains("\r\n1. Leaker.exe (PID 1512), strong evidence\r\n", copy);
        Assert.DoesNotContain("StronglyCorrelated", copy);
    }
}

/// <summary>
/// The window's own details are in the PC's zone (<see cref="CopyText.IncidentDetails(string)"/>, as Fmt.Stamp prints),
/// with a time without an offset read as UTC, one with a Z as UTC, and one with another offset (+02:00) as that instant.
/// Each case sets the PC's zone for this test process (<see cref="PcZone"/>): east of UTC, west of it, and one on UTC in
/// winter and an hour ahead in summer. So code that reads a time through the PC's zone where it should not, or prints
/// UTC as the PC's time, fails here on every PC, CI's UTC included, not only where the PC's own zone is not UTC.
/// </summary>
[Collection(PcZone.Collection)]
public class PcZoneCopyTests
{
    private static TimeZoneInfo Zone(string name) => name switch
    {
        "east" => PcZone.Fixed(330), "west" => PcZone.Fixed(-420), _ => PcZone.UtcInWinter(),
    };

    [Theory]
    [InlineData("east", 7, "2026-07-01 17:30:00 +05:30", "17:30:00")]
    [InlineData("east", 12, "2026-12-01 17:30:00 +05:30", "17:30:00")]
    [InlineData("west", 7, "2026-07-01 05:00:00 -07:00", "05:00:00")]
    [InlineData("west", 12, "2026-12-01 05:00:00 -07:00", "05:00:00")]
    [InlineData("utc in winter", 7, "2026-07-01 13:00:00 +01:00", "13:00:00")]
    [InlineData("utc in winter", 12, "2026-12-01 12:00:00 +00:00", "12:00:00")]
    public void TheWindowsDetailsAreInThePcsZone(string zone, int month, string stamp, string clock)
    {
        string json = $$"""{ "headline": "h", "window": { "fromUtc": "2026-{{month:00}}-01T12:00:00", "onsetUtc": "2026-{{month:00}}-01T12:00:00Z", "toUtc": "2026-{{month:00}}-01T14:00:00+02:00" } }""";
        var at = new DateTime(2026, month, 1, 12, 0, 0, DateTimeKind.Utc);
        using (PcZone.Set(Zone(zone)))
        {
            Assert.Equal($"h\r\nWindow: {stamp} to {stamp} (onset {clock})\r\n", CopyText.IncidentDetails(json));
            Assert.Equal((stamp, clock), (Fmt.Stamp(at), Fmt.Clock(at))); // as the window prints its other times
        }
        Assert.DoesNotContain("PoolWatch test", TimeZoneInfo.Local.Id); // Windows' own zone again
    }
}
