using System.Text;
using PoolWatch.Core;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// Text a standard user's own process chooses (its command line, its exe's name and version resource, its Claude Code
/// transcript and config folder, a volume label) as it reaches report.md, the vendor draft and the CSV files: it stays
/// text, on its own line and in its own cell.
/// </summary>
public class UntrustedTextTests
{
    private const char Rlo = '\u202E'; // RIGHT-TO-LEFT OVERRIDE: makes "exe.txt" read as "txt.exe"

    /// <summary>Every value carries a line break, a backtick, a link, an HTML tag, a pipe, a bidi override and a character reference for an @mention.</summary>
    private static string Untrusted(string marker) =>
        $"{marker}`x`\r\n\n# INJ-{marker}-HEAD\n\n[INJ-{marker}-LINK](https://example.invalid/{marker}) <img src=\"https://example.invalid/{marker}.png\"> a|b {Rlo}gpj.exe &commat;INJ-{marker}-AT";

    /// <param name="company">The suspect exe's CompanyName (the vendor draft's heading); <see cref="Untrusted"/> text when null.</param>
    private sealed class UntrustedHost(string dir, string? company = null) : IIncidentHost
    {
        public string IncidentsDir { get; } = dir;
        public string CheckWrite(string path) => path.StartsWith(IncidentsDir, StringComparison.Ordinal) ? path : throw new UnauthorizedAccessException(path);
        public FileDetails? DescribeFile(string? path) =>
            new(Untrusted("VER"), company ?? Untrusted("COMPANY"), Untrusted("PRODUCT"), Untrusted("DESC"), Untrusted("SIGNER"), Untrusted("SIGSTATUS"));
        public SystemInfo GetSystemInfo() => new()
        {
            Machine = Untrusted("MACHINE"), OsDescription = "Windows 11 Pro 24H2", OsBuild = "26100.1", RamBytes = 64L << 30, LogicalCpus = 16,
            Volumes = [new VolumeInfo(@"E:\", "NTFS", Untrusted("LABEL"), false, "Fixed")],
            FilterDrivers = ["Filter  Instances", "```", "INJ-FENCE-LINE"], AntiVirus = Untrusted("AV"),
        };
        public bool RenderTimeline(TimelineChart chart, string pngPath) => false;
        public void RequestHandleScan() { }
        public void RequestTagScan(IReadOnlyCollection<PoolTag> tags) { }
        public string DisplayUser(string user) => Untrusted("USER");
        public SessionDetails? DescribeSession(Core.Correlation.Suspect suspect) => new(Untrusted("CHAIN"), new SessionContext(null, null, Untrusted("CFG")),
            new TranscriptHit(Untrusted("SESSION"), Untrusted("PROJECT"), Untrusted("WORKFLOW"), Untrusted("SUBAGENT"), null, @"C:\t\" + "a`b].jsonl", Untrusted("COMMAND")), false);
        public void Log(string message) { }
    }

    /// <summary>
    /// The suspect's exe name: Windows allows a backtick, brackets and parentheses in a file name, so a program can be
    /// called this (a line break or a pipe it cannot have).
    /// </summary>
    private const string Image = "le`ak[INJ-IMAGE](x).exe";

    /// <summary>That name as report.md shows it outside a code span.</summary>
    private const string ImageAsText = @"le\`ak\[INJ-IMAGE\](x).exe";

    /// <summary>
    /// A File-tag growth whose top suspect is that process; returns report.md and the incident folder. <paramref
    /// name="traced"/>: a confirmation trace then names it too, and report.md is written again with the
    /// traced-processes table. <paramref name="company"/>: the exe's CompanyName (<see cref="UntrustedHost"/>).
    /// <paramref name="poolTagTxt"/>: a pooltag.txt for the tag's description and owner. <paramref name="image"/> and
    /// <paramref name="commandLine"/>: the suspect's exe name and the start of its command line.
    /// </summary>
    private static (string md, string dir) UntrustedReport(string root, bool traced = false, string? company = null, string? poolTagTxt = null, string? service = null,
        string image = Image, string commandLine = "=SUM(1,2)|x ")
    {
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5 };
        var w = new World();
        w.Registry.Enricher = rec =>
        {
            if (rec.Pid != 100) return;
            rec.CommandLine = commandLine + Untrusted("CMDLINE");
            rec.ImagePath = @"C:\Users\someone\x\le`aker` [INJ-PATH-LINK](https://example.invalid/path)`.exe";
            rec.User = "S-1-5-21-1-2-3-1001";
            if (service is not null) rec.Services = [service];
        };
        var mgr = new IncidentManager(settings, new TagKnowledge(poolTagTxt, null), new UntrustedHost(root, company), w.Registry);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, image);
        long tag = 20 * Settings.MB;
        var rnd = new Random(5);
        for (int i = 0; i < 10; i++) { w.Sample(("File", tag)); mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval)); }
        w.ScanHandles();
        for (int i = 0; i < 20; i++)
        {
            int ops = 2000 + rnd.Next(2000);
            w.Io(100, other: ops, handles: 500);
            tag += ops * 300;
            w.Sample(("File", tag));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
            if (i % 5 == 4) w.ScanHandles();
        }
        mgr.Finish(w.History, w.Now);
        var inc = mgr.All.Single(i => i.Tag == PoolTag.Parse("File"));
        if (traced)
        {
            var file = PoolTag.Parse("File");
            var cap = new CaptureResult
            {
                PoolEventsParsed = true, StartUtc = w.Now.AddMinutes(-1), EndUtc = w.Now, StartMs = Clocks.At(w.Now.AddMinutes(-1)).Ms, EndMs = Clocks.At(w.Now).Ms, Tags = [file],
                Pool = [new CapturePoolUsage(100, image, file, 50 * Settings.MB, 0, 1000, 0, [], ParentPid: 4, FirstAllocUtc: w.Now.AddSeconds(-30))],
            };
            mgr.ApplyCapture(w.History, inc, cap);
        }
        return (File.ReadAllText(Path.Combine(inc.Dir, "report.md")), inc.Dir);
    }

    /// <summary>The cells of a Markdown table row: split at every pipe with an even number of backslashes before it.</summary>
    private static int Cells(string row)
    {
        int pipes = 0;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] != '|') continue;
            int run = 0;
            for (int j = i - 1; j >= 0 && row[j] == '\\'; j--) run++;
            if (run % 2 == 0) pipes++;
        }
        return pipes - 1;
    }

    /// <summary>
    /// A line as Markdown reads it outside its code spans: the text outside (the reference "&amp;amp;" read as the '&amp;'
    /// it names), whether every code span was closed, and whether an unescaped link bracket, HTML tag or character
    /// reference ('&amp;' followed by '#', a letter or a digit, other than "&amp;amp;" and "&amp;lt;") is left outside them.
    /// </summary>
    private static (string outside, bool balanced, bool live) Outside(string line)
    {
        var sb = new StringBuilder();
        bool code = false, live = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (code) { if (c == '`') code = false; continue; }
            if (c == '\\' && i + 1 < line.Length) { sb.Append(line[i + 1]); i++; continue; } // an escaped character is text
            if (c == '`') { code = true; continue; }
            if (c is '[' or ']' or '<') live = true;
            if (c == '&')
            {
                var rest = line.AsSpan(i + 1);
                if (rest.StartsWith("amp;", StringComparison.Ordinal)) { sb.Append('&'); i += 4; continue; } // shows as '&'
                if (!rest.StartsWith("lt;", StringComparison.Ordinal) && rest.Length > 0 && (rest[0] == '#' || char.IsAsciiLetterOrDigit(rest[0]))) live = true;
            }
            sb.Append(c);
        }
        return (sb.ToString(), !code, live);
    }

    [Fact]
    public void SuspectTextInReportMdStaysTextOnItsOwnLine()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root);
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            // PoolWatch's own lines that hold a suspect's text; the text never starts a line of its own.
            string[] starts = ["# ", "- ", "| ", "### ", "## Draft bug report for ", "While ", "1. Start ", "**Actual**: ", "File objects (charged to the opening process) ", "Filter  Instances", "'''", "INJ-FENCE-LINE"];
            var untrustedLines = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("INJ-") || x.l.Contains("example.invalid")).ToList();
            Assert.NotEmpty(untrustedLines);
            foreach (var (l, i) in untrustedLines)
            {
                Assert.True(starts.Any(l.StartsWith), $"line {i} starts with the suspect's text: {l}");
                if (l.StartsWith("# ")) Assert.Equal(0, i); // the headline is the only top-level heading
                if (l is "'''" or "INJ-FENCE-LINE" or "Filter  Instances") continue; // inside the fltmc fence
                var (outside, balanced, live) = Outside(l);
                Assert.True(balanced, $"line {i} leaves a code span open: {l}");
                Assert.False(live, $"line {i} has a live link bracket, HTML tag or character reference outside its code spans: {l}");
                Assert.DoesNotContain(Rlo, l);
                Assert.DoesNotContain("<img", outside.Replace("&lt;", "")); // an HTML tag only ever as the &lt; entity
                Assert.DoesNotContain("://", outside);                      // no web address GitHub would make a link of
            }
            // The exe's name, in every place report.md and the vendor draft name it outside a code span: as text.
            Assert.Contains($"top suspect {ImageAsText} PID 100", lines[0]);
            Assert.Contains($"### 1. {ImageAsText} (PID 100)", lines);
            Assert.Contains(lines, l => l.StartsWith($"While {ImageAsText} ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith($"1. Start {ImageAsText} as `", StringComparison.Ordinal));
            Assert.DoesNotContain(Image, md);
            // The fence of fltmc's lines holds: a line of backticks from outside cannot end it early.
            Assert.Equal(2, lines.Count(l => l == "```"));
            Assert.Contains("'''", lines);
            // Each value is still there for a reader, as text.
            foreach (var marker in new[] { "VER", "COMPANY", "PRODUCT", "SIGNER", "USER", "CHAIN", "CFG", "SESSION", "PROJECT", "WORKFLOW", "SUBAGENT", "COMMAND", "CMDLINE", "LABEL", "AV", "MACHINE" })
            {
                Assert.Contains($"INJ-{marker}-HEAD", md);
                Assert.Contains($"INJ-{marker}-AT", md);
            }
            Assert.Contains("&amp;commat;INJ-COMPANY-AT", md); // a character reference outside a code span: shows as written
            Assert.Contains("`[INJ-COMPANY-LINK](https://example.invalid/COMPANY)`", md); // a word with an address: in a code span
            Assert.Contains("## Draft bug report for COMPANY\\`x\\`", md);
            Assert.Contains("`C:\\Users\\someone\\x\\le'aker' [INJ-PATH-LINK](https://example.invalid/path)'.exe`", md); // inert inside its code span
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AnExeNameCommandLineOrCompanyWithEmphasisMarksIsShownAsWrittenNeverBoldItalicOrStruckThrough()
    {
        // Windows allows '_' and '~' in a file name ('*' it does not); a command line and a version resource can hold all three.
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root, company: "**INJ-BOLD** _INJ-EM_ ~~INJ-STRUCK~~ Ltd", image: "_INJ_EM_~x~.exe",
                commandLine: "run **INJ-CMD-BOLD** __INJ-CMD-EM__ ");
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Contains(@"top suspect \_INJ\_EM\_\~x\~.exe PID 100", lines[0]);
            Assert.Contains(@"### 1. \_INJ\_EM\_\~x\~.exe (PID 100)", lines);
            Assert.Contains(@"## Draft bug report for \*\*INJ-BOLD\*\* \_INJ-EM\_ \~\~INJ-STRUCK\~\~ Ltd", lines);
            Assert.Contains(md, c => c == '~'); // the command line and the names are in the report
            Assert.Contains("INJ-CMD-BOLD", md);
            // Wherever the names and the command line are, outside a code span (where nothing is interpreted) no mark of theirs
            // is left unescaped.
            var marked = lines.Where(l => l.Contains("INJ")).ToList();
            Assert.NotEmpty(marked);
            foreach (string l in marked)
            {
                string bare = UnescapedOutsideCodeSpans(l);
                foreach (string mark in new[] { "*INJ-BOLD", "INJ-BOLD*", "*INJ-CMD", "INJ-CMD-BOLD*", "_INJ", "INJ_", "INJ-EM_", "INJ-CMD-EM_", "~INJ", "INJ-STRUCK~", "_EM_", "~x~" })
                    Assert.DoesNotContain(mark, bare);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>The text of a Markdown line outside its code spans, each escaped character (and its backslash) left out.</summary>
    private static string UnescapedOutsideCodeSpans(string line)
    {
        var sb = new StringBuilder();
        bool code = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (code) { if (c == '`') code = false; continue; }
            if (c == '\\' && i + 1 < line.Length) { i++; continue; }
            if (c == '`') { code = true; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void TheServicesASuspectHostsStayTextOnTheirOwnLine()
    {
        // A service's name is chosen by whoever registered it: on its suspect's "- Services:" line, it starts no line of its own.
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root, service: "Svc " + Untrusted("SVC"));
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            string services = Assert.Single(lines, l => l.StartsWith("- Services: ", StringComparison.Ordinal));
            Assert.StartsWith(@"- Services: Svc SVC\`x\`", services);
            Assert.Contains("INJ-SVC-HEAD", services);
            var (outside, balanced, live) = Outside(services);
            Assert.True(balanced, services);
            Assert.False(live, services);
            Assert.DoesNotContain("://", outside);
            Assert.DoesNotContain(lines, l => l.StartsWith("# INJ-SVC", StringComparison.Ordinal) || l.StartsWith("[INJ-SVC", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// A growth whose top suspect went quiet while the tag kept its rise is marked retained, and its report's "Retained"
    /// line names that suspect's exe: the name stays text there as everywhere else in report.md, and report.json keeps
    /// the note as PoolWatch made it.
    /// </summary>
    [Fact]
    public void ARetainedIncidentsLineKeepsItsSuspectsExeNameAsText()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 1 };
            var w = new World();
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new UntrustedHost(root), w.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, Image);
            long file = 50 * Settings.MB;
            void Step()
            {
                w.Sample(("File", file));
                mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
            }
            for (int i = 0; i < 15; i++) Step();
            for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); file += 800_000; Step(); }
            Incident? inc = null;
            for (int i = 0; i < 90 && (inc = mgr.Closed.SingleOrDefault())?.Retention is null; i++) Step();
            Assert.NotNull(inc?.Retention);
            string note = inc!.Retention!.Note;
            Assert.Contains($" after {Image} (PID 100) went quiet", note); // the note itself holds the name as it is

            string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
            string retained = Assert.Single(md.Split('\n').Select(l => l.TrimEnd('\r')), l => l.StartsWith("- **Retained**: ", StringComparison.Ordinal));
            Assert.Contains($" after {ImageAsText} (PID 100) went quiet", retained);
            var (_, balanced, live) = Outside(retained);
            Assert.True(balanced, retained);
            Assert.False(live, $"the Retained line has a live link bracket outside its code spans: {retained}");
            Assert.DoesNotContain(Image, md); // nowhere in the report as it came
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
            Assert.Equal(note, json.RootElement.GetProperty("retention").GetProperty("note").GetString());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AVendorDraftNamedLikeAMentionMentionsNoOneAndReferencesNothing()
    {
        // The draft is pasted into GitHub: a CompanyName of "@someone" would link and notify that account, "#1" or "GH-2"
        // would reference an issue, and a commit hash would link a commit, unless each is in a code span.
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root, company: "@someone #1 GH-2 a5c3785 Ltd");
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Contains("## Draft bug report for `@someone` `#1` `GH-2` `a5c3785` Ltd", lines);
            string heading = lines.Single(l => l.StartsWith("## Draft bug report for ", StringComparison.Ordinal));
            var (outside, balanced, _) = Outside(heading);
            Assert.True(balanced, heading);
            Assert.DoesNotContain("@", outside);
            Assert.DoesNotContain("#1", outside);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AVendorDraftNamedWithCharacterReferencesShowsThemAsWritten()
    {
        // CommonMark shows a character reference outside a code span as the character it names: "&commat;someone" as
        // "@someone", which GitHub.com then makes a mention of, and "&#x23;1" as "#1", an issue reference. Each '&' that
        // could start one is escaped, so the heading shows the reference as written and names no one and nothing.
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root, company: "&commat;someone &#x40;other &num;1 &#x23;2 &#x61;5c3785 &#64;dec Ltd");
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Contains("## Draft bug report for &amp;commat;someone &amp;#x40;other &amp;num;1 &amp;#x23;2 &amp;#x61;5c3785 `&#64;dec` Ltd", lines);
            string heading = lines.Single(l => l.StartsWith("## Draft bug report for ", StringComparison.Ordinal));
            var (outside, balanced, live) = Outside(heading);
            Assert.True(balanced, heading);
            Assert.False(live, heading); // no '&' outside a code span that could name another character
            Assert.Equal("## Draft bug report for &commat;someone &#x40;other &num;1 &#x23;2 &#x61;5c3785  Ltd", outside); // as a reader sees it
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ATagDescriptionThatStartsLikeAListStaysTextInPlainWords()
    {
        // The "In plain words" line starts with the tag's description when there is a short one (pooltag.txt here, which
        // anyone with administrator rights can edit): it starts no list or heading of its own.
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string poolTag = Path.Combine(root, "pooltag.txt");
            File.WriteAllText(poolTag, "File - ntfs.sys - 1. INJ-PLAIN description\r\n");
            var (md, _) = UntrustedReport(root, poolTagTxt: poolTag);
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int plain = lines.IndexOf("## In plain words");
            Assert.True(plain >= 0, "no In plain words section");
            Assert.StartsWith(@"1\. INJ-PLAIN description (File, ", lines[plain + 2]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheTracedProcessesTableKeepsEachValueInItsCell()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-untrusted-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (md, _) = UntrustedReport(root, traced: true);
            var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int header = lines.FindIndex(l => l.StartsWith("| Process | PID | Started |", StringComparison.Ordinal));
            Assert.True(header >= 0, "no traced-processes table");
            Assert.Equal(9, Cells(lines[header]));
            string row = lines[header + 2];
            Assert.StartsWith($"| {ImageAsText} | 100 |", row);
            Assert.Contains(@"=SUM(1,2)\|x", row); // the command line's pipe, escaped inside its code span
            Assert.Equal(9, Cells(row));
            var (_, balanced, live) = Outside(row);
            Assert.True(balanced, row);
            Assert.False(live, row);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SinceBootMdKeepsTagOwnersDescriptionsAndProcessNamesAsText()
    {
        var hours = new List<SinceBootHour>
        {
            new(new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc), 50 * Settings.MB, [(Untrusted("PROC"), 100, 1.0, 50 * Settings.MB)]),
        };
        var tags = new List<SinceBootTag> { new(PoolTag.Parse("File"), Untrusted("OWNER"), Untrusted("DESC"), 80 * Settings.MB, 30 * Settings.MB, 50 * Settings.MB, hours) };
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        string md = SinceBootReport.ToMarkdown(tags, t0, t0, t0.AddHours(2));
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var untrusted = lines.Where(l => l.Contains("INJ-") || l.Contains("example.invalid")).ToList();
        Assert.Equal(2, untrusted.Count); // the table row and the hour line: no value started a line of its own
        Assert.StartsWith("| `File` | ", untrusted[0]);
        Assert.Equal(5, Cells(untrusted[0]));
        Assert.StartsWith("- ", untrusted[1]);
        foreach (var l in untrusted)
        {
            var (outside, balanced, live) = Outside(l);
            Assert.True(balanced, l);
            Assert.False(live, l);
            Assert.DoesNotContain(Rlo, l);
            Assert.DoesNotContain("://", outside);
        }
        foreach (var marker in new[] { "OWNER", "DESC", "PROC" }) Assert.Contains($"INJ-{marker}-HEAD", md);
    }

    [Theory]
    [InlineData("[a](https://x.example)", "`[a](https://x.example)`")] // a word with a web address: in a code span, no link
    [InlineData("see https://x.example/a now", "see `https://x.example/a` now")]
    [InlineData("WWW.example.com", "`WWW.example.com`")]
    [InlineData("mail me@example.com", "mail `me@example.com`")]
    [InlineData("a|https://x.example", @"a\|`https://x.example`")]  // a pipe stays outside the span, escaped
    [InlineData("https:​//x.example", "`https://x.example`")]  // judged once the formatting character is gone
    [InlineData("x`https://x.example", "`x'https://x.example`")]
    [InlineData("node@20 a.b", "`node@20` a.b")]                   // any word with an '@': GitHub may make a mention or a link of it
    [InlineData("@octocat", "`@octocat`")]                          // an @mention: GitHub links and notifies that account
    [InlineData("Company @octocat Ltd", "Company `@octocat` Ltd")]
    [InlineData("see #1", "see `#1`")]                               // an issue or pull request reference
    [InlineData("fixes owner/repo#12.", "fixes `owner/repo#12.`")]
    [InlineData("C#7 build", "`C#7` build")]
    [InlineData("issue #a", "issue #a")]                             // '#' and a letter: no reference
    [InlineData("GH-26 and gh-7", "`GH-26` and `gh-7`")]
    [InlineData("commit a5c3785 done", "commit `a5c3785` done")]     // a commit hash, 7 to 40 hexadecimal digits
    [InlineData("a5c3785ed8d6a35868bc169f07e40e889087fd2e", "`a5c3785ed8d6a35868bc169f07e40e889087fd2e`")]
    [InlineData("{4d36e968-e325-11ce-bfc1-08002be10318}", "`{4d36e968-e325-11ce-bfc1-08002be10318}`")]
    [InlineData("abc1234 4567890", "`abc1234` `4567890`")]
    [InlineData("abc123 x1234567 a5c3785ed8d6a35868bc169f07e40e889087fd2e0", "abc123 x1234567 a5c3785ed8d6a35868bc169f07e40e889087fd2e0")] // 6, joined to a letter, 41
    [InlineData("a\r\nb", "a  b")]
    [InlineData("a`b", @"a\`b")]
    [InlineData("a|b", @"a\|b")]
    [InlineData("<img src=x>", "&lt;img src=x>")]
    [InlineData("&commat;octocat", "&amp;commat;octocat")]           // a named character reference for '@': shows as written
    [InlineData("&#x40;octocat", "&amp;#x40;octocat")]               // the hex one
    [InlineData("&#64;octocat", "`&#64;octocat`")]                   // the decimal one: '#' and a digit, so in a code span
    [InlineData("&num;1 &#x23;12", "&amp;num;1 &amp;#x23;12")]       // '#' as a reference: no issue reference either
    [InlineData("owner/repo&#x23;12", "owner/repo&amp;#x23;12")]
    [InlineData("user&#x40;example.com", "user&amp;#x40;example.com")]
    [InlineData("&#x61;5c3785", "&amp;#x61;5c3785")]                 // a commit hash spelled with a reference
    [InlineData("&#35;12", "`&#35;12`")]                             // '#' as a decimal reference: '#' and a digit, so in a code span
    [InlineData("&#97;5c3785", "`&#97;5c3785`")]                     // a hex digit as a decimal one: the same
    [InlineData("&é &€", "&é &€")]                                   // no reference starts with a character outside ASCII
    [InlineData("&amp;", "&amp;amp;")]
    [InlineData("AT&T & Co", "AT&amp;T & Co")]                       // an '&' a space follows can start no reference
    [InlineData("ends&", "ends&amp;")]                               // one that ends the text is escaped: what follows it is not known here
    [InlineData("a​&​commat;", "a&amp;commat;")]           // judged once the invisible characters are gone
    [InlineData(@"\[a](u)", @"\\\[a\](u)")]        // a backslash cannot take back the escape
    [InlineData(@"a\|b", @"a\\\|b")]
    [InlineData(@"C:\Users\x", @"C:\Users\x")]      // a path keeps its backslashes
    [InlineData(@"ends\", @"ends\\")]               // nor escape what follows the text
    [InlineData("a\u202Eb\u200Bc", "abc")]           // bidi and zero-width characters are left out
    [InlineData("a\u2028b\0c", "a b c")]
    [InlineData("a\U000E0041\U000E0001b", "ab")]                     // invisible tag characters, above U+FFFF: left out too
    [InlineData("a\U0001D173b", "ab")]                               // a musical formatting character, above U+FFFF
    [InlineData("\U0001F600", "\U0001F600")]                          // a character above U+FFFF that is not formatting stays
    [InlineData("a\U000E0100\U000E0101\U000E01EFb", "ab")]           // variation selectors above U+FFFF (Mn, not Cf): left out
    [InlineData("a︀️b", "ab")]                             // variation selectors 1 and 16
    [InlineData("a͏b᠋c᠏d឴e", "abcde")]           // the combining grapheme joiner, Mongolian and Khmer ones
    [InlineData("aᅟᅠbㅤcﾠd", "abcd")]             // the Hangul fillers (Lo)
    [InlineData("a­b؜c⁥d￰e", "abcde")]           // the soft hyphen, the Arabic letter mark, reserved ones
    [InlineData("a\U0001BCA0b\U000E0FFFc", "abc")]
    [InlineData("áb　c", "áb　c")]               // a visible combining accent and a wide space stay
    [InlineData("**bold**.exe", @"\*\*bold\*\*.exe")]                // emphasis anywhere on the line: never bold or italic
    [InlineData("run *now* x", @"run \*now\* x")]
    [InlineData("_x_ __y__", @"\_x\_ \_\_y\_\_")]
    [InlineData("my_tool_v2.exe", @"my\_tool\_v2.exe")]
    [InlineData("~~gone~~ ~a~", @"\~\~gone\~\~ \~a\~")]               // nor struck through
    [InlineData(@"a\*b\_c", @"a\\\*b\\\_c")]                         // a backslash cannot take back those escapes either
    public void MdTextKeepsOutsideTextAsText(string raw, string expected) => Assert.Equal(expected, MdText.Text(raw));

    [Fact]
    public void ASurrogateWithoutItsPairBecomesTheReplacementCharacter()
    {
        // In a Fact: a theory's data is serialized, which would replace the lone surrogate before the test saw it.
        string lone = "a" + (char)0xD800 + "b" + (char)0xDC00;
        Assert.Equal("a�b�", MdText.Text(lone));
        Assert.Equal("a�b�", MdText.Code(lone));
    }

    [Theory]
    [InlineData("# heading", @"\# heading")]
    [InlineData("  > quote", @"\> quote")]
    [InlineData("- item", @"\- item")]
    [InlineData("12. item", @"12\. item")]
    [InlineData("3) item", @"3\) item")]
    [InlineData("===", @"\===")]
    [InlineData("* item", @"\* item")]
    [InlineData("__init__ x", @"\_\_init\_\_ x")]
    [InlineData("leaker.exe holds it", "leaker.exe holds it")]
    public void MdTextLineStartsNoBlock(string raw, string expected) => Assert.Equal(expected, MdText.Line(raw));

    [Theory]
    [InlineData("a`b", "a'b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\u202Eb", "ab")]
    [InlineData("[x](u) <b>", "[x](u) <b>")] // inert in a code span
    [InlineData("a\U000E0041b\U000E007F", "ab")]
    [InlineData("https://x.example", "https://x.example")] // a code span is never made a link
    [InlineData("@octocat #1", "@octocat #1")]             // nor a mention or a reference
    [InlineData("a\U000E0100b️cㅤd͏e", "abcde")]
    public void MdTextCodeCannotEndItsSpan(string raw, string expected) => Assert.Equal(expected, MdText.Code(raw));

    [Theory]
    [InlineData("a|b", @"a\|b")]
    [InlineData(@"a\|b", @"a\\\|b")]
    [InlineData(@"a\\|b", @"a\\\|b")]
    public void MdTextCodeCellKeepsItsCell(string raw, string expected)
    {
        string cell = MdText.CodeCell(raw);
        Assert.Equal(expected, cell);
        // An odd number of backslashes before every pipe: escaped however the table's reader pairs them.
        for (int i = 0; i < cell.Length; i++)
        {
            if (cell[i] != '|') continue;
            int run = 0;
            for (int j = i - 1; j >= 0 && cell[j] == '\\'; j--) run++;
            Assert.True(run % 2 == 1, cell);
        }
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+1", "'+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(1)", "'@SUM(1)")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\uFF1D1+2", "'\uFF1D1+2")]
    [InlineData("\uFF0B1", "'\uFF0B1")]          // the full-width plus
    [InlineData("\uFF0D1", "'\uFF0D1")]          // the full-width minus
    [InlineData("\uFF20SUM(1)", "'\uFF20SUM(1)")] // the full-width at sign
    [InlineData("\n=1", "\"'\n'=1\"")]
    [InlineData("=HYPERLINK(CHAR(104),CHAR(88))", "\"'=HYPERLINK(CHAR(104),CHAR(88))\"")]
    [InlineData("\r=1", "\"'\r'=1\"")]
    [InlineData("x;=1+2", "\"x;'=1+2\"")]
    [InlineData("y;=cmd|' /c rem'!A0", "\"y;'=cmd|' /c rem'!A0\"")]
    [InlineData("x;\"=1", "\"x;'\"\"=1\"")]
    [InlineData("x\r\n=1", "\"x\r\n'=1\"")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("leaker.exe", "leaker.exe")]
    [InlineData("a=b", "a=b")]
    public void CsvTextCellsRunNoFormula(string raw, string expected) => Assert.Equal(expected, Csv.Escape(raw));

    /// <summary>
    /// A text value in any column of a line, read as a spreadsheet reads it, whether it splits the line at commas or at
    /// semicolons (Excel where the list separator is a semicolon), and whether it holds a value together with quotes only
    /// at the start of a cell, as Excel does, or wherever they are: no cell, and no row a line break in a value starts,
    /// begins with a formula.
    /// </summary>
    [Theory]
    [InlineData(',', false)]
    [InlineData(';', false)]
    [InlineData(',', true)]
    [InlineData(';', true)]
    public void NoCellOrRowStartsWithAFormulaWhetherTheSpreadsheetSplitsAtCommasOrAtSemicolons(char separator, bool quotesAnywhere)
    {
        string[] values =
        [
            "x;=1+2", "y;=cmd|' /c rem'!A0", "x;=1+1;", "x;\"=1+1;", "x\n=1+1;", "x\r\n@SUM(1);", "x\r+1;", "x;\t=1;",
            "x;＝1+2;", "x;＋1;", "x;－1;", "x;＠SUM(1);", "a;;-2;", ";=1;", "=1;=2", "\"x\";=1", "x,=1;",
            "x;\r\n=1;", "x;\n\n=1;", "\"=1;\"=2;",
        ];
        foreach (string v in values)
        foreach (string line in new[] { Csv.Line(v), Csv.Line("a", v), Csv.Line(v, "b"), Csv.Line("a", v, "b") })
        foreach (string cell in SpreadsheetCells(line, separator, quotesAnywhere))
            Assert.False(Csv.IsFormulaStart(cell), $"separator '{separator}', quotes anywhere {quotesAnywhere}: the cell [{cell}] of the line [{line}]");
    }

    /// <summary>
    /// The cells a spreadsheet reads from <paramref name="text"/>, split at <paramref name="separator"/>: a line break
    /// outside held text ends the row (and so the cell), two quotes in held text are one, and a quote starts held text at
    /// the start of a cell only (what follows its closing quote, up to the separator, is added to the cell), or wherever
    /// it is when <paramref name="quotesAnywhere"/>.
    /// </summary>
    private static List<string> SpreadsheetCells(string text, char separator, bool quotesAnywhere)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool held = false, atStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (held)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else held = false;
                continue;
            }
            if (c == '"' && (atStart || quotesAnywhere)) { held = true; atStart = false; continue; }
            if (c == separator || c is '\n' or '\r')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                atStart = true;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                continue;
            }
            cell.Append(c);
            atStart = false;
        }
        cells.Add(cell.ToString());
        return cells;
    }

    [Fact]
    public void CsvNumbersKeepTheirSign()
    {
        Assert.Equal("-1", Csv.Escape(-1));
        Assert.Equal("-5", Csv.Escape(-5L));
        Assert.Equal("-1.5", Csv.Escape(-1.5));
        Assert.Equal("1,-1,'-x", Csv.Line(1, -1, "-x"));
        Assert.Equal(["1", "-1", "'-x"], Csv.Split(Csv.Line(1, -1, "-x")));
    }
}
