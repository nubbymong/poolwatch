using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The public code says what it does in plain words, never which review round, finding or plan item asked for it. The
/// scan reads every file under src/, tests/, tools/, ci/, installer/ and .github/ (dependabot.yml, CODEOWNERS and the
/// issue and pull request templates included) and the root's build files (Directory.Build.props, PoolWatch.slnx,
/// .gitignore), this one aside, but what .gitignore leaves out (the build's output, an editor's files, test results)
/// and images; a file of a kind it cannot read fails it. It fails on:
/// <list type="bullet">
/// <item>a review marker on any line ("(review round 2)", "C4 (review)", "Part 3 review", "Review Part 3", "(round
/// 2)"), and a name in code for a review round, part or addendum ("RoundTwo", "PartThree", "AddendumTests");</item>
/// <item>an id in a comment: a capital letter of any series and one to three digits, then perhaps a small letter, then
/// perhaps a hyphen and digits, with no letter, digit or underscore just before or just after it ("L2:", "(H3, E2)",
/// "Q8-1", "R6b", "S12", "M2 (review S2)", "// M1", "S3.)", "the L2-only path", "per-S12", "review.L2"); or, with no
/// letter, digit or underscore just before it, a digit and a small letter from a to f before a comma, a colon, " ("
/// or the end ("2f: ...", "(2b, ...");</item>
/// <item>an id as a part of the name of a C# type (declared after class, record, record struct, record class, struct,
/// interface or enum) or of a method declared void, Task or ValueTask, a test's among them (a method of another return
/// type is not read): a capital and one to three digits, then perhaps a small letter, at the name's start or after a
/// small letter, a digit or an underscore, and before a capital, an underscore or the name's end
/// ("SettingsSavedBeforeC1Lose...", "TheUiTestRunsWithTheM2Thresholds...", "L2Tests", "record struct L2Found");</item>
/// <item>a plan's item or addendum in a comment ("item 9", "addendum 2 C").</item>
/// </list>
/// Of the ids, only the tokens of <see cref="NotIds"/> are let through, each a name that is not an id, and only where
/// it stands as that list says. A comment is read whole, two ways, and both are scanned. First by where a comment's
/// opening is (<see cref="Comments"/>): the text after "//" or "#" at the start of a line or after a space, inside
/// "/* */", "&lt;# #&gt;", "&lt;!-- --&gt;" and the setup script's "{ }" (a brace and a space) and "(* *)" comments,
/// and a setup script line that starts with ";"; in a Markdown file every line is read as a comment. This reads what
/// looks like a comment inside a string as one too. Then, in a C#, JSON or PowerShell file and the setup script's [Code]
/// section, each comment as ScriptCode's reader of that language reads it (<see cref="LexedComments"/>), which knows the
/// language's strings: so a comment with no space before it ("x = 1;// L2", "$x = 1;# L2", "{L2: ...}" in [Code]) or
/// right after a string ("x = "a"// L2") is found.
/// </summary>
public sealed class NoReviewMarkersTests
{
    /// <summary>The marker forms read on the whole line (a test's name is code, not a comment), each with what it stands for.</summary>
    internal static readonly (string What, Regex Pattern)[] LineMarkers =
    [
        // After a space, so a call such as Display(review) or Pt(9f) is not taken for one.
        ("a review marker", new(@"(?:^|\s)\([Rr]eview(?:\)|,| rounds?\b| S[0-9]| Part\b)|\(security review\)|\b[Rr]eview rounds?\b|\bPart [0-9]+ review\b|\b[Rr]eview Part [0-9]+\b|\(round [0-9]+\)|\(Part [0-9]+\)")),
        ("a test named for a review", new(@"\b(?:class|void)\s+\w*(?:ReviewFixes|ReviewRegression|ReviewTests\b|RoundOne|RoundTwo|RoundThree|Round[0-9]|PartThree|Part[0-9]|Addendum)\w*")),
        // Any other name in code: a local, a field, a parameter.
        ("a name for a review round", new(@"\b\w*(?:[Rr]ound(?:One|Two|Three|[0-9])|PartThree)\b")),
    ];

    /// <summary>The marker forms read in a comment's text, wherever in it they stand.</summary>
    internal static readonly (string What, Regex Pattern)[] CommentMarkers =
    [
        // A capital and one to three digits, perhaps with a small letter and a hyphen and digits after, with no letter,
        // digit or underscore next to it: "L2:", "(H3, E2)", "Q8-1", "R6b", "S12", "S3.)", "// M1", and with a hyphen,
        // a dot or a plus before or after it, or a dollar sign before it, too ("the L2-only path", "per-S12",
        // "review.L2", "fix+S3", "L2+", "$S3").
        ("an id", new(@"(?<!\w)[A-Z][0-9]{1,3}[a-z]?(?:-[0-9]+)?(?!\w)")),
        // "2f: ...", "(2b, ...": a digit and a small letter from a to f, with no letter, digit or underscore before
        // it (a hyphen, a dot, a dollar sign or a plus may be: "per-2f:", "x.2f:", "$2b:", "x+2f:").
        ("an id", new(@"(?<!\w)[0-9][a-f](?:,|:| \(|$)")),
        ("a plan's item", new(@"\b[Ii]tems? [0-9]+[a-z]?\b|\b[Aa]ddendum\b")),
    ];

    /// <summary>
    /// A type declared in code (after class, record, record struct, record class, struct, interface or enum) or a method
    /// declared void, Task or ValueTask ("void X(", "Task X("; a method of another return type is not read), whose name
    /// is read for an id as a part of it: a capital and one to three digits, then perhaps a small letter, after a small
    /// letter, a digit or an underscore or at the name's start, and before a capital, an underscore or the name's end.
    /// </summary>
    private static readonly Regex Declared = new(@"\b(?:class|record(?:\s+(?:struct|class))?|struct|interface|enum|void|Task|ValueTask)\s+([A-Za-z_]\w*)"),
        IdInName = new(@"(?:(?<=[a-z0-9_])|^)[A-Z][0-9]{1,3}[a-z]?(?=[A-Z_]|$)");

    /// <summary>
    /// The tokens shaped like an id that are not one, each with what it is, matched where the token starts (each
    /// pattern starts with \G): only these are let through, and only where they stand so.
    /// </summary>
    internal static readonly (string What, Regex At)[] NotIds =
    [
        ("Unicode's C1 control characters (U+0080 to U+009F), named as such", new(@"\GC1(?= (?:codes?|control|characters?|included)\b)")),
        ("the article A before a process's bit width in a test's name: a 32-bit or a 64-bit process", new(@"\GA(?:32|64)(?=Bit)")),
        ("e2e, end to end, as the CI scripts' file names have it (installer-e2e.ps1), after a small letter in a declared "
            + "name (the test helper InE2e)", new(@"\G(?<=[a-z])E2[eE](?![a-z])")),
        ("a parameter of a Windows Error Reporting event (event 1001 lists its problem signature as P1 to P10), after its "
            + "index in the event's data", new(@"\G(?<=\[[0-9]+\] )P(?:[1-9]|10)\b")),
        ("a function key (the virtual keys VK_F1 to VK_F24), after a modifier and a plus as a shortcut names it (Alt+F4), "
            + "or as the range F1-F24", new(@"\G(?:(?<=\b(?:Ctrl|Alt|Shift|Win)\+)F(?:[1-9]|1[0-9]|2[0-4])\b|F1(?=-F24\b)|(?<=\bF1-)F24\b)")),
        ("one of RAMMap's options, -E0 (empty the priority-0 standby list), after its hyphen", new(@"\G(?<=(?<![\w-])-)E0\b")),
        ("the installer test's own pass between Pass 0 and Pass 1, named so in its header and its output", new(@"\G(?<=\bPass )0b\b")),
    ];

    /// <summary>Whether the token at <paramref name="at"/> in <paramref name="text"/> is in <see cref="NotIds"/>.</summary>
    private static bool NotAnId(string text, int at) => NotIds.Any(n => n.At.Match(text, at).Success);

    /// <summary>How each kind of file is read: by its extension, or its name when it has none.</summary>
    internal enum Syntax { None, CSharp, Pascal, PowerShell, Hash, Xml, Json, Markdown }

    private static readonly Dictionary<string, Syntax> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Syntax.CSharp, [".iss"] = Syntax.Pascal, [".ps1"] = Syntax.PowerShell,
        [".yml"] = Syntax.Hash, [".yaml"] = Syntax.Hash, [".txt"] = Syntax.Hash, ["CODEOWNERS"] = Syntax.Hash, [".gitignore"] = Syntax.Hash,
        [".csproj"] = Syntax.Xml, [".props"] = Syntax.Xml, [".slnx"] = Syntax.Xml, [".manifest"] = Syntax.Xml,
        // The CI scenarios' settings have "//" comments; a strict JSON file has none to read.
        [".json"] = Syntax.Json,
        [".md"] = Syntax.Markdown,
        // JSON Lines (the transcript fixtures): strict JSON, which has no comments.
        [".jsonl"] = Syntax.None,
    };

    /// <summary>Images: no text to read.</summary>
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".ico" };

    /// <summary>A file's kind, by its extension or, with none, its name (null: one the scan cannot read).</summary>
    internal static Syntax? KindOf(string file)
    {
        string ext = Path.GetExtension(file);
        string key = ext.Length > 0 && Path.GetFileNameWithoutExtension(file).Length > 0 ? ext : Path.GetFileName(file);
        return Kinds.TryGetValue(key, out var kind) ? kind : null;
    }

    /// <summary>
    /// What .gitignore leaves out of the repository (the build's output, an editor's files, test results, traces),
    /// which is never read: a folder by its name, a file by the end of its name. The test holds the list to .gitignore.
    /// </summary>
    private static readonly string[] Ignored = ["bin/", "obj/", "*.user", ".vs/", "TestResults/", "artifacts/", "*.etl"];

    private static bool IsIgnored(string relative) => Ignored.Any(p => p.EndsWith('/')
        ? ("/" + relative.Replace('\\', '/')).Contains("/" + p, StringComparison.OrdinalIgnoreCase)
        : relative.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The files read, each with its kind (null: one the scan cannot read): every file under src/, tests/, tools/, ci/,
    /// installer/ and .github/, and the root's build files, this one aside, but what .gitignore leaves out and images.
    /// </summary>
    private static IEnumerable<(string File, Syntax? Kind)> Files()
    {
        string root = TrayIconsTests.RepoRoot();
        foreach (string folder in new[] { "src", "tests", "tools", "ci", "installer", ".github" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            {
                if (IsIgnored(Path.GetRelativePath(root, file))) continue;
                if (Images.Contains(Path.GetExtension(file))) continue;
                if (Path.GetFileName(file) == nameof(NoReviewMarkersTests) + ".cs") continue;
                yield return (file, KindOf(file));
            }
        foreach (string name in new[] { "Directory.Build.props", "PoolWatch.slnx", ".gitignore" })
            yield return (Path.Combine(root, name), KindOf(name));
    }

    /// <summary>A line comment's opening: at the start of the line or after a space, so "https://" or "'#'" is not one.</summary>
    private static readonly Regex SlashComment = new(@"(?:^|\s)//(.*)$"), HashComment = new(@"(?:^|\s)#(?!>)(.*)$"), IssBrace = new(@"\{\s");

    /// <summary>
    /// The comment text on each line of a file of <paramref name="kind"/>, by line number (from 1): every line comment
    /// and the part of a block comment on it; every line of a Markdown file.
    /// </summary>
    internal static List<(int Line, string Text)> Comments(Syntax kind, IReadOnlyList<string> lines)
    {
        var found = new List<(int, string)>();
        if (kind == Syntax.Markdown)
        {
            for (int i = 0; i < lines.Count; i++) if (lines[i].Trim().Length > 0) found.Add((i + 1, lines[i]));
            return found;
        }
        var (open, close) = kind switch
        {
            Syntax.CSharp or Syntax.Json => ("/*", "*/"),
            Syntax.PowerShell => ("<#", "#>"),
            Syntax.Xml => ("<!--", "-->"),
            _ => ((string?)null, (string?)null),
        };
        string? inside = null; // the end of the block comment the line goes on in, if any
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i], rest = line;
            var text = new List<string>();
            while (rest.Length > 0)
            {
                if (inside is not null)
                {
                    int end = rest.IndexOf(inside, StringComparison.Ordinal);
                    text.Add(end < 0 ? rest : rest[..end]);
                    if (end < 0) break;
                    rest = rest[(end + inside.Length)..];
                    inside = null;
                    continue;
                }
                int at = -1, skip = 0;
                string? endsWith = null;
                if (open is not null && rest.IndexOf(open, StringComparison.Ordinal) is var o and >= 0) { at = o; skip = open.Length; endsWith = close; }
                if (kind == Syntax.Pascal)
                {
                    // The block comments of the setup script's code: "{ text }" (an Inno Setup constant such as {app} has
                    // no space after its brace) and "(* text *)".
                    var brace = IssBrace.Match(rest);
                    if (brace.Success && (at < 0 || brace.Index < at)) { at = brace.Index; skip = 1; endsWith = "}"; }
                    int star = rest.IndexOf("(*", StringComparison.Ordinal);
                    if (star >= 0 && (at < 0 || star < at)) { at = star; skip = 2; endsWith = "*)"; }
                }
                var lineComment = kind switch
                {
                    Syntax.PowerShell or Syntax.Hash => HashComment.Match(rest),
                    Syntax.CSharp or Syntax.Pascal or Syntax.Json => SlashComment.Match(rest),
                    _ => Match.Empty,
                };
                if (lineComment.Success && (at < 0 || lineComment.Index < at))
                {
                    text.Add(lineComment.Groups[1].Value);
                    break;
                }
                if (at < 0) break;
                rest = rest[(at + skip)..];
                inside = endsWith;
            }
            if (kind == Syntax.Pascal && line.StartsWith(';')) text.Add(line[1..]);
            if (text.Count > 0) found.Add((i + 1, string.Join(" ", text)));
        }
        return found;
    }

    /// <summary>
    /// The comments of a C#, JSON, PowerShell or setup script file as ScriptCode's reader of its language reads them (it
    /// knows the language's strings, so a comment after one is found, and a "//" or "#" inside one is not a comment), by
    /// line number (from 1): each comment's text, its opening included, on each line it spans. In the setup script only its
    /// [Code] section, up to the next section, is read so (the other sections' braces are Inno Setup constants).
    /// </summary>
    internal static List<(int Line, string Text)> LexedComments(Syntax kind, IReadOnlyList<string> lines)
    {
        string text = string.Join("\n", lines);
        int from = 0;
        IReadOnlyList<(int Start, int End)> spans;
        switch (kind)
        {
            case Syntax.CSharp or Syntax.Json: spans = ScriptCode.CSharpComments(text); break;
            case Syntax.PowerShell: spans = ScriptCode.PowerShellComments(text); break;
            case Syntax.Pascal:
                var code = Regex.Match(text, @"(?mi)^[ \t]*\[Code\][ \t]*$");
                if (!code.Success) return [];
                from = code.Index + code.Length;
                var next = Regex.Match(text[from..], @"(?m)^[ \t]*\[\w+\][ \t]*$");
                spans = ScriptCode.PascalComments(next.Success ? text[from..(from + next.Index)] : text[from..]);
                break;
            default: return [];
        }
        var found = new List<(int, string)>();
        foreach (var (start, end) in spans)
        {
            int line = 1 + text.AsSpan(0, from + start).Count('\n');
            foreach (string part in text[(from + start)..(from + end)].Split('\n')) found.Add((line++, part));
        }
        return found;
    }

    /// <summary>Whether <paramref name="pattern"/> matches <paramref name="text"/> but at a token of <see cref="NotIds"/>.</summary>
    private static bool Holds(Regex pattern, string text) => pattern.Matches(text).Any(m => !NotAnId(text, m.Index));

    /// <summary>Each marker in <paramref name="lines"/> of a file of <paramref name="kind"/>: "line: what: text", once for each line and kind of marker.</summary>
    internal static List<string> Found(IReadOnlyList<string> lines, Syntax kind = Syntax.CSharp)
    {
        var found = new List<(int Line, string What, string Text)>();
        for (int i = 0; i < lines.Count; i++)
        {
            found.AddRange(LineMarkers.Where(m => m.Pattern.IsMatch(lines[i])).Select(m => (i + 1, m.What, lines[i].Trim())));
            if (kind != Syntax.CSharp) continue;
            foreach (Match d in Declared.Matches(lines[i]))
                if (Holds(IdInName, d.Groups[1].Value))
                    found.Add((i + 1, "an id in a name", lines[i].Trim()));
        }
        // Each comment as the patterns of a comment's opening find it, and as the language's reader finds it.
        foreach (var (n, text) in Comments(kind, lines).Concat(LexedComments(kind, lines)))
            found.AddRange(CommentMarkers.Where(m => Holds(m.Pattern, text)).Select(m => (n, m.What, text.Trim())));
        return found.DistinctBy(f => (f.Line, f.What)).OrderBy(f => f.Line).Select(f => $"{f.Line}: {f.What}: {f.Text}").ToList();
    }

    [Fact]
    public void NoCommentTestNameOrScriptCarriesAReviewMarkerAnIdOrAPlansItem()
    {
        string gitignore = Path.Combine(TrayIconsTests.RepoRoot(), ".gitignore");
        Assert.Equal(File.ReadAllLines(gitignore).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')), Ignored);
        Assert.True(IsIgnored(Path.Combine("tests", "PoolWatch.AppTests", "bin", "Debug", "x.dll")) && IsIgnored(Path.Combine("src", "PoolWatch", "PoolWatch.csproj.user")));
        Assert.False(IsIgnored(Path.Combine("src", "PoolWatch", "Binary", "x.cs")));
        var files = Files().ToList();
        Assert.True(files.Count > 400, "the scan reads too few files");
        var unread = files.Where(f => f.Kind is null).Select(f => f.File).ToList();
        Assert.True(unread.Count == 0, "files of a kind the scan cannot read: " + string.Join(", ", unread));
        foreach (string name in new[] { "dependabot.yml", "CODEOWNERS", "pull_request_template.md", "Directory.Build.props", "ci.yml" })
            Assert.Contains(files, f => Path.GetFileName(f.File) == name);
        var found = files.SelectMany(f => Found(File.ReadAllLines(f.File), f.Kind!.Value).Select(m => $"{Path.GetFileName(f.File)}:{m}")).ToList();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found.Take(200)));
    }

    [Fact]
    public void TheScanFindsEachKindOfMarker()
    {
        // Each form the tree had or an earlier scan let through, in each kind of file, and words that are not markers
        // (a feature named Agent review, a round trip, a key's name, a format, a URL, code, Windows versions, and each
        // token of NotIds where it stands).
        (Syntax Kind, string Line)[] marked =
        [
            (Syntax.CSharp, "    /// when it could not. M3 (review S2): if the PoolWatch that started it has gone"),
            (Syntax.CSharp, "        // C4 (review): the silent re-set failed, so we must ask the owner for a click."),
            (Syntax.CSharp, "    // S4 (review round 3): never registered as the logon task then"),
            (Syntax.CSharp, "/// <param name=\"At\">When it was asked for (C1 d, review round 2).</param>"),
            (Syntax.CSharp, "        // 2f: every alert is on the timeline."),
            (Syntax.CSharp, "    /// command lines are scrubbed first (M1)."),
            (Syntax.CSharp, "/// so (Part 3 review) a folder counts only when it is local"),
            (Syntax.CSharp, "        // the one gate in TurnOn (round 2)."),
            (Syntax.CSharp, "public class TimelineRoundTwoTests"),
            (Syntax.CSharp, "public class ReviewFixesPartThreeTests"),
            (Syntax.CSharp, "public class IncidentPartThreeTests"),
            (Syntax.CSharp, "public class AddendumTests"),
            (Syntax.CSharp, "        var roundOne = Folder(Administrators, true);"),
            (Syntax.CSharp, "    private static readonly Rules RoundTwo = Exact;"),
            (Syntax.CSharp, "/// <summary>Review Part 3, addendum 4 A: the page-fault split.</summary>"),
            (Syntax.CSharp, "        // Item 12 (Part 3): an hourly point, kept for a day."),
            (Syntax.CSharp, "    /// when it wrote the file, ResultNotWritten when it could not. M3"),
            (Syntax.CSharp, "    /// <summary>The trace ended (recorded and parsed, or failed: C1 a, the caller calls this).</summary>"),
            (Syntax.CSharp, "        md.AppendLine($\"> {Incidents.Exporter.UnredactedNotice}\").AppendLine(); // M1"),
            (Syntax.CSharp, "/// <summary>Which row to keep at the top of a list after a refresh (C5 item 3: the scroll position survives).</summary>"),
            (Syntax.CSharp, "        // can be reintroduced once the data root is admin-only, S3.)"),
            (Syntax.CSharp, "        // DeeperCapture: what the automatic captures recorded before C1."),
            (Syntax.CSharp, "        using var doc = JsonDocument.Parse(scrubbed); // still valid JSON (the N1 fix)"),
            (Syntax.CSharp, "/// The A/B preset \"lag before vs after a clear\" (addendum 2 C): N minutes of lag probes"),
            (Syntax.CSharp, "        x = 1; /* B2: a block comment */ y = 2;"),
            // An id of any series, and the shapes it takes: a letter after it, a part after a hyphen, three digits.
            (Syntax.CSharp, "        // L2: every action is returned to the caller"),
            (Syntax.CSharp, "        // Every action is returned (H3, E2)."),
            (Syntax.CSharp, "        // Every action is returned (E2)."),
            (Syntax.CSharp, "        // the Q8-1 case"),
            (Syntax.CSharp, "        // as R6b said"),
            (Syntax.CSharp, "        // S12 refuses that"),
            (Syntax.CSharp, "        // M2 (review S2) moved it"),
            (Syntax.CSharp, "        // D53 approved it, then K104"),
            (Syntax.CSharp, "        // the A3 fix"),
            (Syntax.CSharp, "        // the C1 fix: control"),
            // Next to a hyphen, a dot, a plus or a dollar sign, each line with one id: a hyphen after it or before
            // it, a dot before it, a plus before it or after it, a dollar sign before it.
            (Syntax.CSharp, "        // the L2-only path"),
            (Syntax.CSharp, "        // per-S12 it is refused"),
            (Syntax.CSharp, "        // the M2-era thresholds"),
            (Syntax.CSharp, "        // as in R6b-style checks"),
            (Syntax.CSharp, "        // see the review.L2 item"),
            (Syntax.CSharp, "        // see the fix+S3 item"),
            (Syntax.CSharp, "        // the L2+ pair"),
            (Syntax.CSharp, "        // the $S3 value"),
            // A line comment with no space before it: after a semicolon, a closing bracket, a digit, a letter, a comma or an
            // opening brace.
            (Syntax.CSharp, "        x = 1;// L2: the count"),
            (Syntax.CSharp, "        Run(x)// L2: the count"),
            (Syntax.CSharp, "        x = 1// L2: the count"),
            (Syntax.CSharp, "        return \"a\" + x// L2: the count"),
            (Syntax.Json, "  \"a\": 1,// L2: the count"),
            (Syntax.PowerShell, "$x = 1;# L2: the count"),
            (Syntax.PowerShell, "Stop-It $x)# L2: the count"),
            (Syntax.PowerShell, "if ($x) {# L2: the count"),
            // After a string or a character, as the language's reader finds it.
            (Syntax.CSharp, "        x = \"a\"// L2: the count"),
            (Syntax.CSharp, "        x = 'a'// L2: the count"),
            (Syntax.CSharp, "        x = @\"a\"\"b\"/* L2 */;"),
            (Syntax.Json, "  \"a\": \"b\"// L2: the count"),
            (Syntax.PowerShell, "$x = 'a'# L2: the count"),
            // A digit and a small letter from a to f, then a colon, after a space, a hyphen, a dot, a dollar sign or a
            // plus.
            (Syntax.CSharp, "        // 0b: the next pass"),
            (Syntax.CSharp, "        // per-2f: the next step"),
            (Syntax.CSharp, "        // see the x.2f: step"),
            (Syntax.CSharp, "        // the $2b: value"),
            (Syntax.CSharp, "        // the x+2f: step"),
            // An id in a test's, a type's or a helper's name.
            (Syntax.CSharp, "    public void SettingsSavedBeforeC1LoseTheOldCpuTraceDefault()"),
            (Syntax.CSharp, "    public void TheUiTestRunsWithTheM2ThresholdsAndTheHourlyClear()"),
            (Syntax.CSharp, "public sealed class L2Tests"),
            (Syntax.CSharp, "    public async Task R6bHolds()"),
            (Syntax.CSharp, "    private static void Check_S12_Refused() { }"),
            (Syntax.CSharp, "internal sealed record H3Case(int A);"),
            (Syntax.CSharp, "    internal readonly record struct L2Found(string Value);"),
            (Syntax.CSharp, "public sealed record class H3Case(int A);"),
            (Syntax.CSharp, "    public async ValueTask R6bHolds()"),
            (Syntax.PowerShell, "# The CI runs PoolWatch elevated from the build output: S4 refuses that"),
            (Syntax.PowerShell, "        # its baseline and record the pool size at that moment. C1: the trip's CPU trace is held back"),
            (Syntax.PowerShell, "    # Longer ramp: the leak is still allocating. C1: a"),
            (Syntax.PowerShell, "        # Item 9: every core busy at the highest priority makes a 15 ms sleep wake up late"),
            (Syntax.PowerShell, "          # S4 (review round 2): native DLLs ship below the program"),
            (Syntax.Hash, "      # M2: the e2e scenarios"),
            (Syntax.Hash, "# L3 (supply chain): weekly update pull requests for the NuGet packages"),
            (Syntax.Hash, "* @owner # H3"),
            (Syntax.Hash, "# L2 the checks the tests pin"),
            (Syntax.Pascal, "  { B1: open asking for DELETE only }"),
            (Syntax.Pascal, "  // N3 the uninstaller's own code"),
            (Syntax.Pascal, "; Q1 the setup's sections"),
            (Syntax.Xml, "  <!-- linked for R4 -->"),
            (Syntax.Xml, "    <!-- L3 (supply chain): every project's package graph is pinned -->"),
            (Syntax.Json, "  // Diagnostics scenarios: the M2 CI cadence"),
            (Syntax.Json, "  \"a\": 1, /* S2 */ \"b\": 2"),
            (Syntax.Markdown, "- [ ] The change has a test (H3)."),
            (Syntax.Markdown, "<!-- S12: what this changes -->"),
        ];
        Assert.All(marked, m => Assert.True(Found([m.Line], m.Kind).Count > 0, $"not found: {m.Line}"));
        // A block comment goes on over the lines after its opening, up to its end.
        Assert.NotEmpty(Found(["  { The exit codes the cleanup can end with,", "    as S2 would say }", "  x := 1;"], Syntax.Pascal));
        Assert.NotEmpty(Found(["x = 1; /* the codes", "   as S2 would say */"], Syntax.CSharp));
        Assert.NotEmpty(Found(["<#", "  M3: the helper", "#>"], Syntax.PowerShell));
        Assert.NotEmpty(Found(["<!--", "  L3: the lock files", "-->"], Syntax.Xml));
        Assert.Empty(Found(["  { The codes }", "  Result := S2;"], Syntax.Pascal));
        // In the setup script's [Code], each comment as the Pascal reader reads it: a brace with or without a space after
        // it (not one in a string), a "//" with no space before it; outside [Code], a brace and a space open one.
        Assert.NotEmpty(Found(["[Code]", "  {L2: the uninstaller's own code}", "  x := 1;"], Syntax.Pascal));
        Assert.NotEmpty(Found(["[Code]", "  S := 'a'// L2: the count"], Syntax.Pascal));
        Assert.NotEmpty(Found(["[Code]", "  x := 1;// L2: the count"], Syntax.Pascal));
        Assert.NotEmpty(Found(["[Code]", "  S := 'it''s'{L2}"], Syntax.Pascal));
        Assert.NotEmpty(Found(["[Code]", "  x := 1; {as S2", "    would say}"], Syntax.Pascal));
        Assert.Empty(Found(["[Code]", "  Result := ExpandConstant('{app}\\S3') + '{tmp}' + {#AppVersion};", "  x := 1; {the codes}"], Syntax.Pascal));
        Assert.Empty(Found(["[Files]", "Source: \"{#PublishDir}\\S3.exe\"; DestDir: \"{app}\"", "[Code]", "  x := 1;"], Syntax.Pascal));
        Assert.NotEmpty(Found(["[Files]", "Source: \"x\"; DestDir: \"{app}\" { L2: the files }"], Syntax.Pascal));
        (Syntax Kind, string Line)[] plain =
        [
            (Syntax.CSharp, "    internal void Display(AgentReview? review)"),
            (Syntax.CSharp, "    public void TheReviewSaysWhatWillBeTurnedOffWhatIsRefusedAndWhatIsLeftAsItIs()"),
            (Syntax.CSharp, "    public void EveryKindRoundTrips()"),
            (Syntax.CSharp, "/// <summary>\"Ctrl+Alt+M\" to RegisterHotKey's modifier flags and virtual key (letters, digits, F1-F24).</summary>"),
            (Syntax.CSharp, "        // A change here needs an independent security review before it merges."),
            (Syntax.CSharp, "        string s = $\"{n:N0} starts, {m:X8}\"; // the count and the code"),
            (Syntax.CSharp, "        var url = \"https://example.com/S3\";"),
            (Syntax.CSharp, "        var x = M1(a); var y = S3.Value;"),
            (Syntax.CSharp, "        // Windows 11 24H2 and 25H2, x64 and ARM64, SHA256, UTF-8, Win32, U+0080, 0x8664, IPv4 and the m2-config.json thresholds"),
            (Syntax.CSharp, "    public void TheWindowFitsWholeOnA1024By600DesktopAtEveryScaling()"),
            (Syntax.CSharp, "    private const long Tick0 = 7_000_000; // milliseconds since Windows started, at that time"),
            // NotIds, each where it stands.
            (Syntax.CSharp, "    /// control character (a carriage return, an escape, C1 codes), line and paragraph separator"),
            (Syntax.CSharp, "    [InlineData(\"a\\rb\", \"a?b\")] // control characters, C1 included"),
            (Syntax.CSharp, "    public void ReadsTheCurrentDirectoryOfA64BitProcess()"),
            (Syntax.CSharp, "    public void SkipsA32BitProcess()"),
            (Syntax.CSharp, "        void InE2e(string text) { Assert.Contains(text, e2e); pinned.Add(text); }"),
            (Syntax.CSharp, "            // [0] bucket, [1] bucket type, [2] event name, ... [5] P1 (the app for APPCRASH)."),
            (Syntax.PowerShell, "Assert-True ($tsP -gt 0.2) (\"tag-sum paged {0:N0} MB plausible\" -f ($tsP / 1MB))"),
            (Syntax.PowerShell, "# ui-config.json: m2-config.json's thresholds plus the hourly automatic clear."),
            (Syntax.PowerShell, "# the launcher now. Pass 0b: 1.0.0-beta.1's self-contained zip (pinned the same way)"),
            (Syntax.CSharp, "    /// The owner closes setup on its first run (its close button, Esc, Alt+F4): asks whether to close it"),
            (Syntax.CSharp, "    /// <summary>RAMMap's \"empty\" options: -Ew working sets, -Et standby list, -E0 priority-0 standby.</summary>"),
            (Syntax.CSharp, "        // Ctrl+F12, Shift+F1 and Win+F24"),
            // A "#" that PowerShell or YAML reads as a part of its word, not a comment.
            (Syntax.PowerShell, "Write-Host a#L2 'b#S3'"),
            (Syntax.Hash, "key: a#L2"),
            (Syntax.Pascal, "  Result := ExpandConstant('{app}\\S3');"),
            (Syntax.Json, "    \"url\": \"https://example.com/S3\","),
            (Syntax.Markdown, "Fixes #123 on Windows 11 24H2 (build 26100), x64 and ARM64."),
        ];
        Assert.All(plain, p => Assert.True(Found([p.Line], p.Kind).Count == 0, $"taken for a marker: {p.Line}"));
        // Where they stand only: the same tokens elsewhere are ids.
        Assert.NotEmpty(Found(["    public void TheA64CaseHolds()"]));
        Assert.NotEmpty(Found(["    public void TheA32CaseHolds()"]));
        Assert.NotEmpty(Found(["        // C1: control characters"]));
        Assert.NotEmpty(Found(["        // E2 refused it"]));
        Assert.NotEmpty(Found(["        // P1: the first part"]));
        Assert.NotEmpty(Found(["        // F4 asked for it"]));
        Assert.NotEmpty(Found(["        // the F25 key"]));
        Assert.NotEmpty(Found(["        // per F1-F23"]));
        Assert.NotEmpty(Found(["        // E0 asked for it"]));
        Assert.NotEmpty(Found(["        // x-E0 asked for it"]));
        Assert.NotEmpty(Found(["        // E2e asked for it"]));
        Assert.NotEmpty(Found(["    public void E2eRuns()"]));
        Assert.NotEmpty(Found(["        // pass 0b: the zip"]));
        // Each kind of file the tree has is read, and a kind the scan does not know is not passed over.
        Assert.Equal(Syntax.Hash, KindOf("CODEOWNERS"));
        Assert.Equal(Syntax.Hash, KindOf(".gitignore"));
        Assert.Equal(Syntax.Hash, KindOf("dependabot.yml"));
        Assert.Equal(Syntax.Markdown, KindOf("pull_request_template.md"));
        Assert.Equal(Syntax.Xml, KindOf("PoolWatch.slnx"));
        Assert.Null(KindOf("README"));
        Assert.Null(KindOf("notes.rtf"));
    }
}
