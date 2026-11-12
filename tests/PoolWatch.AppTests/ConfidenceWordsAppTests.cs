using PoolWatch.Analysis;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core;
using PoolWatch.Core.Util;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// An owner's confidence is in the words of Pool causes' Confidence column (<see cref="PlainWords.Confidence"/>) in the
/// three texts the window and the bundle build from owners: a growing tag's details (<c>MainForm.TagDetailsOwner</c>), Copy
/// summary of a tag row (<c>MainForm.TagSummary</c>, each owner by <c>MainForm.OwnerDetails</c>) and a vendor bundle's
/// incidents.md (<c>BundleBuilder.IncidentsMarkdown</c>). A source check pins that the window and the bundle use them.
/// </summary>
public class ConfidenceWordsAppTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>The words of every older wording, none of which these texts may hold now.</summary>
    private static readonly string[] OlderWords =
        ["strongly correlated", "weak signal", "confirmed by trace", "(strong)", "(some)", "(weak)", ": strong\r", ": some\r", ": weak\r", ": strong (", ": some (", ": weak ("];

    public static TheoryData<Confidence> Levels => new(Enum.GetValues<Confidence>());

    public static TheoryData<Confidence, bool> LevelsAndCorrelationOnly()
    {
        var data = new TheoryData<Confidence, bool>();
        foreach (var c in Enum.GetValues<Confidence>()) { data.Add(c, false); data.Add(c, true); }
        return data;
    }

    private static OwnerSuspect Owner(string name, Confidence c, bool correlationOnly = false, bool meaningful = true) => new()
    {
        Owner = new Owner(OwnerKind.App, "app:" + name.ToLowerInvariant(), name), Members = [], Identities = [], Meaningful = meaningful, Confidence = c,
        CorrelationOnly = correlationOnly,
    };

    /// <summary>The owners in the order a ranking holds them: <paramref name="top"/> first, then one at each other level.</summary>
    private static List<OwnerSuspect> Owners(Confidence top, bool correlationOnly = false) =>
        [Owner("Leaker", top, correlationOnly), .. Enum.GetValues<Confidence>().Where(c => c != top).OrderByDescending(c => c).Select((c, i) => Owner($"Other{i}", c))];

    private static TagViewData View(List<OwnerSuspect> owners) =>
        new(PoolTag.Parse("Reff"), "Reff", new TagAnswer("grew", owners, 1, 0), owners, [], T0, T0, T0);

    [Theory]
    [MemberData(nameof(LevelsAndCorrelationOnly))]
    public void ATagsDetailsNameAnOwnersConfidenceAsTheConfidenceColumnDoes(Confidence level, bool correlationOnly)
    {
        var o = Owner("Leaker", level, correlationOnly);
        o.Plain.Add("file activity: 3.5 M operations");
        Assert.Equal($"\r\n2. Leaker: {PlainWords.Confidence(level)}{(correlationOnly ? " (correlation only)" : "")}\r\n   - file activity: 3.5 M operations\r\n",
            MainForm.TagDetailsOwner(2, o));
    }

    [Theory]
    [MemberData(nameof(LevelsAndCorrelationOnly))]
    public void CopySummaryOfATagRowNamesEachOwnersConfidenceAsTheConfidenceColumnDoes(Confidence level, bool correlationOnly)
    {
        var owners = Owners(level, correlationOnly);
        string copy = MainForm.TagSummary(View(owners));
        string only = correlationOnly ? " (correlation only: the confirmation trace did not see it allocate this tag)" : "";
        Assert.Contains($"\r\n1. Leaker: {PlainWords.Confidence(level)}{only} (App)\r\n", copy);
        for (int i = 1; i < owners.Count; i++)
            Assert.Contains($"\r\n{i + 1}. {owners[i].Name}: {PlainWords.Confidence(owners[i].Confidence)} (App)\r\n", copy);
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, copy, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void ABundlesIncidentsMdNamesEachSuspectsConfidenceAsTheConfidenceColumnDoes(Confidence level)
    {
        var owners = Owners(level);
        owners.Insert(1, Owner("Hidden", Confidence.StronglyCorrelated, meaningful: false)); // not meaningful: not listed
        var incident = new Incident
        {
            Id = "20261101-060000-Reff", Dir = "x", Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OpenedUtc = T0, OnsetUtc = T0,
            StartBytes = 1L << 30, PeakBytes = 2L << 30, Owners = owners,
        };
        string md = BundleBuilder.IncidentsMarkdown([incident]);
        var suspects = md.Split(Environment.NewLine).Where(l => l.StartsWith("- Suspect: ", StringComparison.Ordinal)).ToList();
        var listed = owners.Where(o => o.Meaningful).Take(3).ToList();
        Assert.Equal(listed.Select(o => $"- Suspect: {o.Name} ({PlainWords.Confidence(o.Confidence)})"), suspects);
        Assert.Equal($"- Suspect: Leaker ({PlainWords.Confidence(level)})", suspects[0]);
        Assert.All(OlderWords, old => Assert.DoesNotContain(old, md, StringComparison.Ordinal));
    }

    /// <summary>A Markdown line outside its code spans, each escaped character left out; null when it leaves a code span open.</summary>
    private static string? OutsideCodeSpans(string line)
    {
        var sb = new System.Text.StringBuilder();
        bool code = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (code) { if (c == '`') code = false; continue; }
            if (c == '\\' && i + 1 < line.Length) { i++; continue; }
            if (c == '`') { code = true; continue; }
            sb.Append(c);
        }
        return code ? null : sb.ToString();
    }

    /// <summary>
    /// A suspect's summary holds names other programs chose: its owner's name, an exe's name (Windows allows a backtick,
    /// brackets and parentheses in one) and a role taken from a command line. In the bundle's incidents.md they stay
    /// text, through the escape report.md gives the same summary (<see cref="MdText.Text"/>): no link, image or HTML tag,
    /// no bold, no code span left open, and nothing GitHub would make a link, a mention or an issue reference of.
    /// </summary>
    [Fact]
    public void ABundlesIncidentsMdKeepsASuspectsNamesAndRoleAsText()
    {
        var owner = new OwnerSuspect
        {
            Owner = new Owner(OwnerKind.App, "app:inj", "INJ **Owner**"), Members = [], Identities = [], Meaningful = true, Confidence = Confidence.Correlated,
        };
        owner.Members.Add(new Suspect
        {
            Key = new PoolWatch.Core.Model.ProcessKey(100, 1), Image = "pwsh.exe", StartUtc = T0,
            Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
            Lifecycle = new LifecycleSignal(false, false, false, []),
        });
        owner.Identities.Add(new ProcessIdentity("le`ak[INJ-IMAGE](x).exe", "command: [INJ-ROLE](https://example.invalid/r) @someone #12 <img src=x>", owner.Owner, false));
        Assert.Equal("INJ **Owner**: le`ak[INJ-IMAGE](x).exe · command: [INJ-ROLE](https://example.invalid/r) @someone #12 <img src=x> under INJ **Owner**", owner.Summary);
        var incident = new Incident
        {
            Id = "20261101-060000-Reff", Dir = "x", Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OpenedUtc = T0, OnsetUtc = T0,
            StartBytes = 1L << 30, PeakBytes = 2L << 30, Owners = [owner],
        };
        string md = BundleBuilder.IncidentsMarkdown([incident]);
        string line = Assert.Single(md.Split(Environment.NewLine), l => l.StartsWith("- Suspect: ", StringComparison.Ordinal));
        Assert.Equal($"- Suspect: {MdText.Text(owner.Summary)} ({PlainWords.Confidence(Confidence.Correlated)})", line);
        Assert.Equal(@"- Suspect: INJ \*\*Owner\*\*: le\`ak\[INJ-IMAGE\](x).exe · command: `[INJ-ROLE](https://example.invalid/r)` `@someone` `#12` &lt;img src=x> under INJ \*\*Owner\*\* (some evidence)", line);
        string? outside = OutsideCodeSpans(line);
        Assert.NotNull(outside); // every code span is closed
        foreach (string live in new[] { "[", "]", "<", "*", "`", "://", "@", "#1" }) Assert.DoesNotContain(live, outside);
        Assert.DoesNotContain(owner.Summary, md); // nowhere as it came
    }

    [Fact]
    public void TheWordsAreTheColumnsOwn()
    {
        Assert.StartsWith("\r\n1. Leaker: strong evidence (correlation only)\r\n", MainForm.TagDetailsOwner(1, Owner("Leaker", Confidence.StronglyCorrelated, correlationOnly: true)));
        Assert.Contains("\r\n1. Leaker: some evidence (App)\r\n", MainForm.TagSummary(View([Owner("Leaker", Confidence.Correlated)])));
        Assert.Equal("- Suspect: Leaker (confirmed by a trace)", BundleBuilder.SuspectLine(Owner("Leaker", Confidence.ConfirmedByTrace)));
        Assert.Equal("- Suspect: Leaker (weak evidence)", BundleBuilder.SuspectLine(Owner("Leaker", Confidence.Weak)));
    }

    /// <summary>
    /// The window's details and Copy summary of a tag row, and the bundle's incidents.md, are built by the functions above:
    /// read from the source, as the window and the bundle need the whole app to run. The bundle hands incidents.md the
    /// newest sample's time, for the line of a trace's incident whose trace has not finished
    /// (<c>RecommendationInputsBuilderTests.ATracesIncidentWithNoSpanIsListedInTheBundleByWhatItsReportSays</c>).
    /// </summary>
    [Fact]
    public void TheWindowAndTheBundleBuildTheseTextsWithThem()
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        string main = File.ReadAllText(Path.Combine(src, "Ui", "MainForm.cs")), bundle = File.ReadAllText(Path.Combine(src, "Analysis", "BundleBuilder.cs"));
        string details = MainWindowLayoutTests.Block(main, "private string TagDetails(CauseRow row)");
        Assert.Contains("foreach (var o in v.Owners.Where(o => o.Meaningful).Take(3)) sb.Append(TagDetailsOwner(++n, o));", details);
        Assert.DoesNotContain("Confidence", details);
        Assert.Contains("() => TagSummary(ranked)", MainWindowLayoutTests.Block(main, "private CopyInfo? CauseCopy(object? key)"));
        string export = MainWindowLayoutTests.Block(bundle, "public static string Export(MonitorEngine engine, AnalysisPipeline analysis, RedactionInfo? redaction)");
        Assert.Contains("files[\"incidents.md\"] = IncidentsMarkdown(analysis.Incidents.All, h.Count > 0 ? h.Times[^1] : null);", export);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(export, "incidents\\.md"));
        Assert.DoesNotContain("Confidence", export);
    }
}
