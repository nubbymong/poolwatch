using System.Text.Json;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Report;

namespace PoolWatch.Tests;

/// <summary>Report an issue: the redaction backstop, the incident digest, the issue text and the address with its limit.</summary>
public class IssueReportTests
{
    private static readonly RedactionInfo Alice = new("alice", "BIGBOX", [@"C:\Users\alice"], "CORP", "S-1-5-21-1-2-3-1001");

    private static string Redact(string text) => IssueReport.Redact(text, Alice);

    private static string Root(string variable, string fallback) => (Environment.GetEnvironmentVariable(variable) is { Length: > 3 } v ? v : fallback).TrimEnd('\\');

    // ------------------------------------------------------------------ redaction

    [Fact]
    public void TheExportRedactionRunsFirst()
    {
        string r = Redact(@"CORP\alice on BIGBOX opened C:\Users\alice\Documents with --token abcdef123456 (S-1-5-21-1-2-3-1001)");
        Assert.Equal(@"<DOMAIN>\<USER> on <MACHINE> opened <HOME>\Documents with --token <SECRET> (<SID>)", r);
    }

    [Theory]
    [InlineData(@"C:\Users\bob\Documents\x.txt", @"C:\Users\<USER>\Documents\<PATH>")]
    [InlineData(@"D:\Users\John Smith\Desktop", @"D:\Users\<USER>\Desktop")]
    [InlineData(@"seen in C:/Users/bob/AppData", @"seen in C:/Users/<USER>/AppData")]
    [InlineData(@"C:\Users\bob", @"C:\Users\<USER>")]
    public void AnotherAccountsProfileFolderLosesItsName(string text, string expected) => Assert.Equal(expected, Redact(text));

    [Theory]
    [InlineData(@"C:\Users\alice\source\repos\secret-falcon\build.ps1", @"<HOME>\source\<PATH>")]
    [InlineData(@"C:\Users\alice\notes.txt", @"<HOME>\<PATH>")]
    [InlineData(@"C:\Users\alice\OneDrive - Contoso Ltd\Clients\Acme Merger\plan.docx", @"<HOME>\OneDrive - <ORG>\<PATH>")]
    [InlineData(@"in C:\Users\alice\OneDrive - Contoso Ltd, synced", @"in <HOME>\OneDrive - <ORG>, synced")]
    [InlineData(@"C:\Users\bob\AppData\Local\Packages\Contoso.Tool_8wekyb3d8bbwe\x.db", @"C:\Users\<USER>\AppData\Local\<PATH>")]
    [InlineData(@"C:\Users\bob\Documents\", @"C:\Users\<USER>\Documents\")]
    [InlineData("synced to OneDrive - Contoso Ltd every hour", "synced to OneDrive - <ORG> every hour")]
    public void OnlyAProfilesStandardFoldersStay(string text, string expected) => Assert.Equal(expected, Redact(text));

    [Theory]
    [InlineData(@"D:\src\secret-project\build.log", "<PATH>")]
    [InlineData(@"open D:\My Projects\app\Program.cs now", "open <PATH> now")]
    [InlineData(@"see E:\work\notes.txt.", "see <PATH>.")]
    [InlineData(@"(F:\x\y), then", "(<PATH>), then")]
    [InlineData(@"C:\ProgramData\Other\cache.bin", "<PATH>")]
    [InlineData(@"C:\WindowsApps\x", "<PATH>")]
    [InlineData(@"C:\Windows.old\Users\x", "<PATH>")]
    [InlineData(@"C:\ProgramData\PoolWatch-old\x", "<PATH>")]
    public void AnyOtherAbsolutePathIsHidden(string text, string expected) => Assert.Equal(expected, Redact(text));

    [Theory]
    [InlineData(@"D:\Projects\Project Falcon", "<PATH>")]
    [InlineData(@"D:\Clients\Acme Corp, then it froze", "<PATH>, then it froze")]
    [InlineData(@"D:\x\My File.txt was open", "<PATH> was open")]
    [InlineData(@"D:\Builds\Build 2 failed", "<PATH> failed")]
    [InlineData(@"saved in D:\x\a.txt. Then PoolWatch Showed it", "saved in <PATH>. Then PoolWatch Showed it")]
    [InlineData(@"\\nas01\Team Files\Budget Plan.xlsx is gone", "<SHARE> is gone")]
    public void ALastNameWithSpacesIsHiddenWhole(string text, string expected) => Assert.Equal(expected, Redact(text));

    [Fact]
    public void WindowsProgramFilesAndPoolWatchsDataFolderStay()
    {
        string windows = Root("SystemRoot", @"C:\Windows"), programs = Root("ProgramFiles", @"C:\Program Files"),
               programs86 = Root("ProgramFiles(x86)", @"C:\Program Files (x86)"), data = Root("ProgramData", @"C:\ProgramData") + @"\PoolWatch";
        string text = $@"{windows}\System32\drivers\refs.sys, {programs}\PoolWatch\PoolWatch.exe, {programs86}\Windows Kits\10\Debuggers\x64\triage\pooltag.txt, " +
                      $@"{data}\incidents\20261028-091200-Reff-66666552, {programs}, \\?\{windows}\System32 and D:\";
        Assert.Equal(text, Redact(text));
    }

    [Theory]
    [InlineData(@"\\fileserver\team\notes.txt", "<SHARE>")]
    [InlineData(@"copied to \\nas01\backup.", "copied to <SHARE>.")]
    [InlineData(@"BIGBOX.corp.contoso.com shares \\BIGBOX\Finance\budget.xlsx", "<MACHINE>.<DOMAIN> shares <SHARE>")]
    [InlineData("S-1-5-21-111-222-333-500 and S-1-5-21-999-888-777-1002 and S-1-5-21-111-222-333", "<SID> and <SID> and <SID>")]
    [InlineData(@"\\?\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\", @"\\?\<ID>\")]
    [InlineData("Volume{3F2504E0-4F89-11D3-9A0C-0305E82C3301} and {3f2504e0-4f89-11d3-9a0c-0305e82c3301} and 3f2504e0-4f89-11d3-9a0c-0305e82c3301", "<ID> and <ID> and <ID>")]
    [InlineData("volume serial 1A2B-3C4D", "volume serial <SERIAL>")]
    [InlineData("mail me at jane.doe+pw@example.co.uk", "mail me at <EMAIL>")]
    [InlineData("from 192.0.2.10 and 198.51.100.7.", "from <IP> and <IP>.")]
    [InlineData("fe80::1c2d:3e4f:5a6b:7c8d%12 or 2001:0db8:85a3:0000:0000:8a2e:0370:7334 or ::1", "<IP> or <IP> or <IP>")]
    [InlineData("adapter 00-00-5E-00-53-01 or 00:00:5e:00:53:0a", "adapter <MAC> or <MAC>")]
    public void SharesIdsSerialsMailAndAddressesAreReplaced(string text, string expected) => Assert.Equal(expected, Redact(text));

    [Theory]
    [InlineData("PoolWatch 1.0.0 on build 26200.8655 at 09:12:30, a well-known dead-beef case")]
    [InlineData("between 2024-2025, about 1000-2000 handles, run as S-1-5-18")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new")]
    [InlineData("Reff (ReFS file records, refs.sys): 2.1 GB, up 1.2 GB in the last hour")]
    public void OrdinaryTextStays(string text) => Assert.Equal(text, Redact(text));

    // ------------------------------------------------------------------ the incident digest

    private static string Report(string kind, string headline, string? plainAnswer, params object[] suspects) =>
        JsonSerializer.Serialize(new
        {
            notice = Exporter.UnredactedNotice, id = "20261028-091200-Reff-66666552", kind, tag = "Reff", headline, plainAnswer,
            suspects, events = new[] { new { image = "node.exe", detail = "C:\\Users\\alice\\proj\\run.ps1 --password hunter2" } },
        });

    private static object Suspect(string displayName, string image, string confidence, string commandLine) => new
    {
        rank = 1, pid = 7, image, displayName, commandLine, confidence,
        evidence = new[] { "handles of type File rose 40,000 in the window", $"command line: {commandLine}" },
        ancestry = new[] { "explorer.exe", "WindowsTerminal.exe" },
    };

    [Fact]
    public void TheDigestKeepsTheAnswerAndNeverACommandLineOrASession()
    {
        const string display = "node.exe · command: deploy --token sk-proj-abcdefghijklmnop1234 under Claude Code session secret-proj (PID 7)";
        string json = Report("Growth",
            $"`Reff` (refs.sys FCB_DATA) grew 6.1 GB in 3 h 0 min; top suspect {display} PID 7, started 09:12:00 by Claude Code session 1234abcd / workflow wf_0001 (strongly correlated)",
            "Reff grew 6.1 GB in 3 h. Most likely cause: Claude Code session secret-proj (PID 7) (strong).",
            Suspect(display, "node.exe", "StronglyCorrelated", "node deploy.js --password hunter2"),
            Suspect("Codex.exe", "Codex.exe", "Correlated", "\"C:\\Program Files\\WindowsApps\\Codex.exe\" --type=renderer"),
            Suspect("svchost.exe (WSearch)", "svchost.exe", "Weak", "svchost.exe -k netsvcs"),
            Suspect("fourth.exe", "fourth.exe", "Weak", "fourth.exe"));
        string digest = IssueReport.IncidentDigest(json)!;

        foreach (var hidden in new[] { "hunter2", "sk-proj", "deploy", "command", "secret-proj", "1234abcd", "wf_0001", "handles of type", "WindowsTerminal", "netsvcs", "--type", "fourth.exe", "NOT REDACTED" })
            Assert.DoesNotContain(hidden, digest);
        Assert.StartsWith("Incident 20261028-091200-Reff-66666552 (growth): `Reff` (refs.sys FCB_DATA) grew 6.1 GB in 3 h 0 min; top suspect " +
                          "node.exe under Claude Code session (PID 7) PID 7, started 09:12:00 (strongly correlated).", digest);
        Assert.Contains("Most likely cause: Claude Code session (PID 7) (strong).", digest);
        Assert.EndsWith("Top suspects: node.exe under Claude Code session (PID 7) (strong evidence); Codex.exe (some evidence); svchost.exe (WSearch) (weak evidence)", digest);
    }

    [Fact]
    public void ARoleOrALabelGoesWholeWhereverItEnds()
    {
        const string pwsh = @"pwsh.exe · command: Copy-Item secret-falcon under D:\x\tok3n";
        string json = Report("Growth", $"`Reff` (refs.sys FCB_DATA) grew 1.0 GB in 1 h 0 min; top suspect {pwsh} PID 9 (weak signal)", null,
            Suspect(pwsh, "pwsh.exe", "Weak", "pwsh -c Copy-Item"),
            Suspect("node.exe [command: deploy under Claude Code session secret-proj (PID 9)]", "node.exe", "Weak", "node deploy.js"),
            Suspect("svchost.exe (WSearch) [background service started by updater.exe]", "svchost.exe", "Weak", "svchost.exe -k netsvcs"));
        string digest = IssueReport.IncidentDigest(json)!;

        foreach (var hidden in new[] { "secret-falcon", "tok3n", "Copy-Item", "deploy", "secret-proj", "updater" })
            Assert.DoesNotContain(hidden, digest);
        Assert.Contains("; top suspect pwsh.exe PID 9 (weak signal).", digest);
        Assert.EndsWith("Top suspects: pwsh.exe (weak evidence); node.exe (weak evidence); svchost.exe (WSearch) (weak evidence)", digest);
    }

    [Fact]
    public void AHeadlineThatDoesNotNameTheTopSuspectAsWrittenLeavesItOut()
    {
        string json = Report("Growth", "`Reff` grew 1.0 GB in 1 h 0 min; top suspect node.exe · command: an older name PID 9 (weak signal)", null,
            Suspect("node.exe · command: a newer name", "node.exe", "Weak", "node x.js"));
        Assert.StartsWith("Incident 20261028-091200-Reff-66666552 (growth): `Reff` grew 1.0 GB in 1 h 0 min.\nTop suspects: node.exe", IssueReport.IncidentDigest(json));
    }

    [Fact]
    public void ADropIsDigestedAsARelease()
    {
        string digest = IssueReport.IncidentDigest(Report("Release", "`Reff` (refs.sys FCB_DATA) dropped 1.2 GB at 09:12:00", null))!;
        Assert.Equal("Incident 20261028-091200-Reff-66666552 (release): `Reff` (refs.sys FCB_DATA) dropped 1.2 GB at 09:12:00.", digest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"id\": \"x\"}")]
    [InlineData("{\"headline\": 5}")]
    public void AFileThatIsNotAnIncidentReportHasNoDigest(string json) => Assert.Null(IssueReport.IncidentDigest(json));

    // ------------------------------------------------------------------ the text

    private static readonly IssueFacts Facts = new("1.0.0", "installer (setup.exe)", "Windows 11 Pro 25H2, build 26200.8655, Insider", "x64", 64L << 30, 1,
        "9.6 GB (paged and non-paged pool), up 1.2 GB in the last hour", "on, every 1 h; last clear at 09:12 (standby list, automatic) freed 4.7 GB",
        "off; no saved test result", "PoolWatch itself (last 10 min): CPU 0.21% of one core, 64 MB, 1.2 MB/h written",
        ["File system metadata (Reff, refs.sys): 2.1 GB"], "Incident x (growth): it grew.");

    [Fact]
    public void TheDetailsArePlainLinesWithTheIncidentLast()
    {
        Assert.Equal("PoolWatch 1.0.0, installer (setup.exe)\nWindows: Windows 11 Pro 25H2, build 26200.8655, Insider, x64\nMemory: 64.0 GB\nDev Drives: 1\n" +
                     "Kernel pool now: 9.6 GB (paged and non-paged pool), up 1.2 GB in the last hour\n" +
                     "Automatic cache clear: on, every 1 h; last clear at 09:12 (standby list, automatic) freed 4.7 GB\nToken-leak guard: off; no saved test result\n" +
                     "PoolWatch itself (last 10 min): CPU 0.21% of one core, 64 MB, 1.2 MB/h written\nLargest kernel pool tags:\n" +
                     "- File system metadata (Reff, refs.sys): 2.1 GB\n\nIncident x (growth): it grew.\n", IssueReport.Details(Facts));
        Assert.DoesNotContain("Incident", IssueReport.Details(Facts with { Incident = null, TopTags = [] }));
        Assert.DoesNotContain("|", IssueReport.Details(Facts));
    }

    [Fact]
    public void TheFullTextHasTheTitleTheWordsAndWhatPoolWatchSaw()
    {
        Assert.Equal("It froze\n\nWhat happened\nline one\nline two\n\nWhat PoolWatch saw\nPoolWatch 1.0.0\n",
            IssueReport.FullText("It froze", "line one\r\nline two", "PoolWatch 1.0.0\n"));
        Assert.Equal("It froze\n", IssueReport.FullText(" It froze ", "  ", ""));
    }

    [Theory]
    [InlineData("Windows 10 Pro 25H2", "26200.8655", true, "Windows 11 Pro 25H2, build 26200.8655, Insider")]
    [InlineData("Windows 10 Pro 22H2", "19045.5011", false, "Windows 10 Pro 22H2, build 19045.5011")]
    [InlineData("Windows 11 Home 24H2", "26100.4061", false, "Windows 11 Home 24H2, build 26100.4061")]
    [InlineData("Windows 10 Enterprise", "?", false, "Windows 10 Enterprise, build ?")]
    public void WindowsIsNamedByItsBuild(string product, string build, bool insider, string expected) =>
        Assert.Equal(expected, IssueReport.WindowsLine(product, build, insider));

    // ------------------------------------------------------------------ the address

    /// <summary>A field's value, unescaped, or null when the address does not fill it in.</summary>
    private static string? Field(string url, string name)
    {
        string query = url[(url.IndexOf('?') + 1)..];
        return query.Split('&').SingleOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal)) is { } p ? Uri.UnescapeDataString(p[(name.Length + 1)..]) : null;
    }

    [Fact]
    public void TheAddressFillsTheShortFieldsWithEveryValueEscaped()
    {
        string url = IssueReport.Url("Title & \"more\" #1", "1.0.0", "Windows 11 Pro 25H2, build 26200.8655");
        Assert.Equal(IssueReport.NewIssueUrl + "?template=bug_report.yml&title=Title%20%26%20%22more%22%20%231&version=1.0.0" +
                     "&windows=Windows%2011%20Pro%2025H2%2C%20build%2026200.8655", url);
        // The description and the evidence are pasted: an address Explorer passes on whole has no room for them.
        Assert.Null(Field(url, "what"));
        Assert.Null(Field(url, "evidence"));
        Assert.Equal("first line", Field(IssueReport.Url(" first line \r\nsecond", "", ""), "title"));
        Assert.Null(Field(IssueReport.Url("t", "", ""), "version"));
    }

    [Fact]
    public void ATitleThatFillsTheRoomIsKeptWholeAndALongerOneIsCut()
    {
        int room = IssueReport.MaxUrlLength - IssueReport.Url("", "1.0.0", "Windows 11").Length;
        string exact = new('a', room);
        string url = IssueReport.Url(exact, "1.0.0", "Windows 11");
        Assert.Equal(IssueReport.MaxUrlLength, url.Length);
        Assert.Equal(exact, Field(url, "title"));

        url = IssueReport.Url(exact + "a", "1.0.0", "Windows 11");
        Assert.Equal(IssueReport.MaxUrlLength, url.Length);
        Assert.Equal(new string('a', room - 3) + "...", Field(url, "title"));

        // Cut at a space when there is one in the second half.
        url = IssueReport.Url(new string('a', room - 10) + " " + new string('b', 10), "1.0.0", "Windows 11");
        Assert.Equal(new string('a', room - 10) + "...", Field(url, "title"));
    }

    [Fact]
    public void TheVersionAndWindowsGoInWholeOrNotAtAll()
    {
        string url = IssueReport.Url("It froze", "1.0.0", new string('w', IssueReport.MaxUrlLength));
        Assert.Null(Field(url, "windows"));
        Assert.Equal("1.0.0", Field(url, "version"));
        Assert.Equal("It froze", Field(url, "title"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("\u00e9")]           // two UTF-8 bytes: six characters escaped
    [InlineData("\u65e5\u672c")]     // three bytes each
    [InlineData("\U0001F600")]       // a surrogate pair: twelve characters escaped
    public void TheAddressIsNeverOverTheLimitAndNeverSplitsACharacter(string unit)
    {
        string huge = string.Concat(Enumerable.Repeat(unit, 2_000));
        // Versions of 1 to 12 characters move the title's room through every remainder of a 12-character escape, and the
        // second title puts the surrogate pairs at odd offsets. A split pair would come back as U+FFFD.
        for (int v = 1; v <= 12; v++)
            foreach (var title in new[] { huge, "a" + huge })
            {
                string url = IssueReport.Url(title, new string('1', v), "Windows 11");
                Assert.True(url.Length <= IssueReport.MaxUrlLength, $"{url.Length} characters");
                Assert.DoesNotContain('\uFFFD', Field(url, "title")!);
                Assert.EndsWith("...", Field(url, "title"));
            }
        Assert.True(IssueReport.Url(huge, huge, huge).Length <= IssueReport.MaxUrlLength);
    }
}
