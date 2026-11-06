using System.Text.Json;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Report;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// One wording for a confidence: Pool causes' Confidence column's ("confirmed by a trace", "strong evidence", "some
/// evidence", "weak evidence", <see cref="PlainWords.Confidence"/>) wherever a person reads one: the plain answer, the
/// headline, report.md, an incident's details and Copy summary, and the issue report. report.json's <c>confidence</c>
/// stays the name, and a report an earlier build wrote (with "strongly correlated", "weak signal", "(strong)") still
/// loads with its headline and plain answer as written, and its suspects' confidence is read from the same names.
/// </summary>
public sealed class ConfidenceWordsTests : IDisposable
{
    private static readonly PoolTag File = PoolTag.Parse("File");
    private const long MB = Settings.MB;
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-words-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The words of every older wording, none of which a report may hold now.</summary>
    private static readonly string[] OlderWords =
        ["strongly correlated", "weak signal", "confirmed by trace", "(strong)", "(some)", "(weak)", "(strong,", "(some,", "(weak,", "| correlated |", "(correlated)"];

    public static TheoryData<Confidence> Levels => new(Enum.GetValues<Confidence>());

    private static Suspect Process(int pid, string image, Confidence c, int rank) => new()
    {
        Key = new ProcessKey(pid, 1000 + pid), Image = image, DisplayName = image, Confidence = c, Rank = rank, Score = 1.0 / rank,
        StartUtc = World.T0,
        Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
        Lifecycle = new LifecycleSignal(false, false, false, []),
    };

    private static OwnerSuspect OwnerOf(string name, Confidence c, long explained) => new()
    {
        Owner = new Owner(OwnerKind.App, "app:" + name, name), Members = [], Identities = [], Meaningful = true, Confidence = c, ExplainedBytes = explained,
    };

    /// <summary>
    /// A growth incident whose top suspect and top owner have <paramref name="top"/>, with one suspect and one owner at each
    /// other level, written as PoolWatch writes it; its report.md and report.json.
    /// </summary>
    private (Incident inc, string md, string json) Write(Confidence top, bool traced = false)
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        long tag = 50 * MB;
        for (int i = 0; i < 20; i++) { w.Sample(("File", tag)); tag += i >= 5 ? 4 * MB : 0; }
        var others = Enum.GetValues<Confidence>().Where(c => c != top).OrderByDescending(c => c).ToList();
        var inc = new Incident
        {
            Id = "20261101-060000-File-656c6946", Dir = Path.Combine(_dir, "incident"), Tag = File, Kind = IncidentKind.Growth,
            OpenedUtc = World.T0.AddSeconds(30), OnsetUtc = World.T0.AddSeconds(10), EndUtc = w.Now, LastRiseUtc = w.Now, LastAlertUtc = w.Now,
            StartBytes = 50 * MB, PeakBytes = tag, CurrentBytes = tag,
            Window = new CorrelationWindow(World.T0, World.T0.AddSeconds(10), w.Now, w.Now),
            Suspects = [Process(100, "leaker.exe", top, 1), .. others.Select((c, i) => Process(200 + i, $"other{i}.exe", c, i + 2))],
            Owners = [OwnerOf("Leaker", top, tag - 50 * MB), .. others.Select((c, i) => OwnerOf($"Other{i}", c, 0))],
        };
        if (traced) inc.TraceOwners = [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:Tracer", "Tracer") }];
        new IncidentWriter(new TagKnowledge(null, null), new FakeHost(_dir), w.Registry).Write(w.History, inc, []);
        return (inc, System.IO.File.ReadAllText(Path.Combine(inc.Dir, "report.md")), System.IO.File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ThePlainAnswerNamesTheCausesConfidenceAsTheConfidenceColumnDoes(Confidence level)
    {
        var top = OwnerOf("Leaker", level, 300 * MB);
        var next = OwnerOf("Stream Deck", Confidence.Correlated, 0);
        var a = OwnerRanking.Answer(new TagInfo("nt", "File objects", "builtin"), File, 0, 1, 300 * MB, "the last hour", [top, next]);
        Assert.Equal($"File objects (File, non-paged) grew 300 MB in the last hour. Most likely cause: Leaker ({PlainWords.Confidence(level)}). " +
                     "Next: Stream Deck (some evidence).", a.Line);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AReportNamesEveryConfidenceInTheConfidenceColumnsWords(Confidence level)
    {
        var (inc, md, json) = Write(level);
        string words = PlainWords.Confidence(level);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        // The headline, in report.json and as report.md's title and its draft bug report's "Actual".
        string top = $"; top suspect leaker.exe PID 100, started {Fmt.Clock(World.T0)} ({words})";
        Assert.EndsWith(top, r.GetProperty("headline").GetString());
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("# ", lines[0]);
        Assert.EndsWith(top, lines[0]);
        Assert.Contains(lines, l => l.StartsWith("**Actual**: ", StringComparison.Ordinal) && l.EndsWith(top + ".", StringComparison.Ordinal));
        // The plain answer, in report.json and report.md.
        Assert.Contains($"Most likely cause: Leaker ({words}).", r.GetProperty("plainAnswer").GetString());
        Assert.Contains($"Most likely cause: Leaker ({words}).", md);
        // In plain words: the three owners, each with its confidence.
        Assert.Contains($"- **Leaker** ({words}): ", md);
        foreach (var o in inc.Owners!.Skip(1).Take(2)) Assert.Contains($"- **{o.Name}** ({PlainWords.Confidence(o.Confidence)}): ", md);
        // The suspects table: every row's Confidence cell.
        Assert.Contains($"| 1 | leaker.exe | 100 | {words} | ", md);
        foreach (var s in inc.Suspects.Skip(1)) Assert.Contains($"| {s.Rank} | {s.DisplayName} | {s.Key.Pid} | {PlainWords.Confidence(s.Confidence)} | ", md);
        // report.json keeps the names, which are what PoolWatch reads back.
        Assert.Equal(level.ToString(), r.GetProperty("suspects")[0].GetProperty("confidence").GetString());
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, md, StringComparison.Ordinal));
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, json, StringComparison.Ordinal));
    }

    /// <summary>With a confirmation trace, its owners and the correlation-only ones keep their own suffix.</summary>
    [Fact]
    public void ATracedReportNamesItsOwnersAndTheCorrelationOnlyOnesInTheSameWords()
    {
        var (_, md, json) = Write(Confidence.StronglyCorrelated, traced: true);
        Assert.Contains("- **Tracer** (confirmed by a trace): 0 B net in 0 process(es), 0 allocations, 0 frees.", md);
        Assert.Contains("Correlation only (no allocation in the trace):", md);
        Assert.Contains("- Leaker (strong evidence, correlation only): ", md);
        Assert.Contains("- Other0 (confirmed by a trace, correlation only): ", md);
        Assert.Contains("- Other1 (some evidence, correlation only): ", md);
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, md, StringComparison.Ordinal));
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, json, StringComparison.Ordinal));
    }

    /// <summary>
    /// What reads a report back takes one with the new words and the same report as an earlier build wrote it (its headline
    /// and plain answer in the older words, everything else the same) alike: each keeps its headline and answer as written,
    /// and the suspects' confidence comes from report.json's names, in the column's words for both.
    /// </summary>
    [Fact]
    public void AReportWithTheOlderWordsReadsAsOneWithTheNewWords()
    {
        var (_, _, json) = Write(Confidence.StronglyCorrelated);
        string started = Fmt.Clock(World.T0);
        string headlineNow = $"; top suspect leaker.exe PID 100, started {started} (strong evidence)";
        string headlineThen = $"; top suspect leaker.exe PID 100, started {started} (strongly correlated)";
        Assert.Contains(headlineNow, json);
        Assert.Contains("Most likely cause: Leaker (strong evidence).", json);
        Assert.Contains("Next: Other0 (confirmed by a trace).", json); // the plain answer's only other confidence
        // The same report.json as the earlier build wrote it: only the headline and the plain answer said it otherwise,
        // and nothing in it is left in the new words.
        string older = json.Replace(headlineNow, headlineThen, StringComparison.Ordinal)
                           .Replace("Most likely cause: Leaker (strong evidence).", "Most likely cause: Leaker (strong).", StringComparison.Ordinal)
                           .Replace("Next: Other0 (confirmed by a trace).", "Next: Other0 (confirmed by trace).", StringComparison.Ordinal);
        Assert.DoesNotContain("evidence)", older);
        Assert.DoesNotContain("by a trace", older);

        foreach (var (file, headline, answer) in new[] { (json, headlineNow, "(strong evidence)"), (older, headlineThen, "(strong)") })
        {
            // Report an issue's digest of the incident.
            var digest = IssueReport.IncidentDigest(file);
            Assert.NotNull(digest);
            var lines = digest!.Split('\n');
            Assert.StartsWith("Incident 20261101-060000-File-656c6946 (growth): `File` (", lines[0]);
            Assert.EndsWith(headline + ".", lines[0]);
            Assert.Contains($"Most likely cause: Leaker {answer}.", lines[1]);
            Assert.Equal("Top suspects: leaker.exe (strong evidence); other0.exe (confirmed by a trace); other1.exe (some evidence)", lines[2]);
            // The Pool causes details, and Copy summary (report.json as it is, but for the suspects' confidence).
            foreach (string copy in new[] { CopyText.IncidentDetails(file, TimeZoneInfo.Utc), CopyText.IncidentSummary(file) })
            {
                Assert.Contains(headline + "\r\n", copy);
                Assert.Contains($"Most likely cause: Leaker {answer}.", copy);
                Assert.Contains("\r\n1. leaker.exe (PID 100), strong evidence\r\n", copy);
                Assert.Contains("\r\n2. other0.exe (PID 200), confirmed by a trace\r\n", copy);
                Assert.Contains("\r\n3. other1.exe (PID 201), some evidence\r\n", copy);
                Assert.Contains("\r\n4. other2.exe (PID 202), weak evidence\r\n", copy);
            }
        }
    }

    /// <summary>
    /// No other switch maps a confidence to text: the only switch arm from a confidence to a string literal
    /// (<c>Confidence.X =&gt; "..."</c>) under src is <see cref="PlainWords.Confidence"/>'s. It finds that form only; a ternary,
    /// a table or a confidence's name passed on as text would pass it, which is what the tests of each place are for.
    /// </summary>
    [Fact]
    public void NoOtherSwitchMapsAConfidenceToText()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var arm = new System.Text.RegularExpressions.Regex(@"Confidence\.(ConfirmedByTrace|StronglyCorrelated|Correlated|Weak)\s*=>\s*""");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => arm.IsMatch(System.IO.File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).ToList();
        Assert.Equal(["PoolWatch.Core/Util/PoolStatus.cs"], found);
        Assert.True(arm.IsMatch("Confidence.StronglyCorrelated => \"strong\""), "the check itself finds an arm that returns text");
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "PoolWatch.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
