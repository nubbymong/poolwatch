using System.Reflection;
using System.Text.RegularExpressions;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// PoolWatch's one table of exit codes (<see cref="ExitCodes"/>): each code defined there once, each value with one
/// meaning; the launcher built from the same file; the setup's script naming and handling each code the uninstaller's
/// cleanup can end with; and docs/TECHNICAL.md listing them all.
/// </summary>
public sealed class ExitCodesTests
{
    /// <summary>Every code of the table, by its name.</summary>
    private static Dictionary<string, int> Table() =>
        typeof(ExitCodes).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);

    /// <summary>
    /// The codes --uninstall-cleanup can end with, through the launcher: the launcher's own when PoolWatch did not run, the
    /// gate's refusal, a command line PoolWatch did not take, a refused folder, and the cleanup's own. <see
    /// cref="ExitCodes.Done"/>, <see cref="ExitCodes.NotAllPutBack"/> and <see cref="ExitCodes.OtherDataFolder"/> let the
    /// uninstaller go on (CleanupDone); each other one says why it removed nothing (NotRemovedText).
    /// </summary>
    private static readonly string[] FromCleanup =
    [
        nameof(ExitCodes.Done), nameof(ExitCodes.Failed), nameof(ExitCodes.BadCommandLine), nameof(ExitCodes.NotInAdminFolder),
        nameof(ExitCodes.DataNotSafe), nameof(ExitCodes.StillRunning), nameof(ExitCodes.NotAllPutBack), nameof(ExitCodes.NamesNotSafe),
        nameof(ExitCodes.NotStarted), nameof(ExitCodes.Refused), nameof(ExitCodes.OtherDataFolder),
    ];

    /// <summary>The cleanup's codes after which the uninstaller removes PoolWatch's files.</summary>
    private static readonly string[] CleanupDone = [nameof(ExitCodes.NotAllPutBack), nameof(ExitCodes.OtherDataFolder)];

    /// <summary>
    /// The codes no step of the setup or the uninstaller can get, each with the only steps that give it: the tray app's
    /// and a headless run's "already running", the clear-task step's, --service-off's, the token-leak guard's helper's
    /// (which the guard reads, not the setup) and --selftest's.
    /// </summary>
    private static readonly string[] NeverSeenBySetup =
    [
        nameof(ExitCodes.AlreadyRunning), nameof(ExitCodes.ClearTaskKept), nameof(ExitCodes.NotAllTurnedOff),
        nameof(ExitCodes.ResultNotWritten), nameof(ExitCodes.ChecksFailed),
    ];

    [Fact]
    public void EveryExitCodeHasAValueOfItsOwn()
    {
        var table = Table();
        Assert.True(table.Count >= 16, string.Join(", ", table.Keys));
        var shared = table.GroupBy(c => c.Value).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Key))}").ToList();
        Assert.True(shared.Count == 0, "exit codes that share a value: " + string.Join("; ", shared));
        Assert.Equal(0, ExitCodes.Done);
        // A new code is either one the uninstaller's cleanup can end with, which the setup's script then has to handle, or
        // one no step of the setup or the uninstaller gets: it has to be put in one of the two lists here.
        Assert.Equal(table.Keys.Order(), FromCleanup.Concat(NeverSeenBySetup).Order());
    }

    /// <summary>
    /// No exit code is defined anywhere else: no source file but ExitCodes.cs declares a constant named for an exit code,
    /// and the launcher is built from the same file, so its own code and those it passes on come from the one table.
    /// </summary>
    [Fact]
    public void TheCodesAreDefinedOnlyInTheTableWhichTheLauncherIsBuiltFrom()
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src");
        var elsewhere = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith(Path.Combine("Engine", "ExitCodes.cs"), StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\bconst\s+int\s+(\w*(?:Exit|NotWritten|NotStarted|Refused)\w*)\s*=").Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .ToList();
        Assert.True(elsewhere.Count == 0, "exit codes defined outside ExitCodes.cs: " + string.Join("; ", elsewhere));
        string project = File.ReadAllText(Path.Combine(src, "PoolWatch.Launcher", "PoolWatch.Launcher.csproj"));
        Assert.Contains("<Compile Include=\"..\\PoolWatch\\Engine\\ExitCodes.cs\" Link=\"ExitCodes.cs\" />", project);
        // The launcher's failure is the table's NotStarted.
        string fail = UninstallerTests.OneLine(MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(src, "PoolWatch.Launcher", "Program.cs")),
            "private static int Fail(string[] args, string why)"));
        Assert.EndsWith("return ExitCodes.NotStarted;", fail);
    }

    /// <summary>
    /// The setup's script names each code the cleanup can end with, with the table's name and value, and handles each: the
    /// three after which the uninstaller goes on in CleanupDone, every other one in a branch of its own in NotRemovedText,
    /// on that code alone and with words of its own, which say why the uninstaller removed nothing. Its other numbered
    /// constants are the setup's own (Windows' 3010 and 1603), and no two of its codes, nor its own values for a cleanup it
    /// could not start, share a value. CleanupDone is held here as all that its body holds; it and NotRemovedText are also
    /// held whole, as every routine of the script is (<see cref="UninstallerTests.EveryRoutineOfTheScriptIsHeldWhole"/>).
    /// </summary>
    [Fact]
    public void TheSetupNamesAndHandlesEveryCodeTheCleanupCanEndWith()
    {
        var table = Table();
        string code = ScriptCode.Pascal(UninstallerTests.CodeSection());
        var constants = Regex.Matches(code, @"(?m)^\s*(Exit\w+) = (-?\d+);").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        foreach (string name in FromCleanup.Where(n => n != nameof(ExitCodes.Done)))
            Assert.True(constants.TryGetValue("Exit" + name, out int value) && value == table[name], $"the setup's Exit{name} is not {table[name]}");
        Assert.Equal(FromCleanup.Where(n => n != nameof(ExitCodes.Done)).Select(n => "Exit" + n).Append("ExitFinishesAtRestart").Append("ExitNotFinished").Order(),
            constants.Keys.Order());
        Assert.Equal((3010, 1603), (constants["ExitFinishesAtRestart"], constants["ExitNotFinished"]));
        var own = new[] { -1, int.Parse(Regex.Match(code, @"(?m)^\s*NotInPlace = (-\d+);").Groups[1].Value) };
        Assert.Equal(own.Length + constants.Count + 1, own.Concat(constants.Values).Append(ExitCodes.Done).Distinct().Count());

        // All that CleanupDone's body holds: the three codes after which the uninstaller goes on, and no other statement.
        Assert.Equal(["Result := (Code = 0) or (Code = ExitNotAllPutBack) or (Code = ExitOtherDataFolder)"], UninstallerTests.Block("CleanupDone"));
        // NotRemovedText is one if ... else if ... chain: each code has a branch of its own, on that code alone (it is named
        // nowhere else in the routine), that sets words of its own (no two branches set the same text).
        string texts = UninstallerTests.OneLine(ScriptCode.PascalRoutine(code, "NotRemovedText"));
        var words = new Dictionary<string, string>();
        foreach (string name in FromCleanup.Except(CleanupDone).Where(n => n != nameof(ExitCodes.Done)))
        {
            Assert.True(Regex.Matches(texts, $@"\bExit{name}\b").Count == 1, $"NotRemovedText names Exit{name} more than once, or not at all");
            var branch = Regex.Match(texts, $@"\bif Code = Exit{name} then Result := (?<text>.+?) else ");
            Assert.True(branch.Success, $"NotRemovedText has no branch of its own for Exit{name}");
            words[name] = branch.Groups["text"].Value;
        }
        var same = words.GroupBy(w => w.Value).Where(g => g.Count() > 1).Select(g => string.Join(" and ", g.Select(w => w.Key))).ToList();
        Assert.True(same.Count == 0, "NotRemovedText gives the same words to " + string.Join("; ", same));
        foreach (string name in NeverSeenBySetup)
            Assert.DoesNotContain("Exit" + name, code);
    }

    /// <summary>
    /// No exit code is written as a number: in every source file that gives an exit code (one that names
    /// <see cref="ExitCodes"/>, the launcher's included), no <c>return</c>, expression body, <c>Environment.Exit</c> or
    /// <c>ExitCode</c> takes a literal number, 0 included (<see cref="ExitCodes.Done"/>).
    /// </summary>
    [Fact]
    public void NoFileThatGivesAnExitCodeWritesOneAsANumber()
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src");
        char sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") && !f.EndsWith($"{sep}ExitCodes.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ExitCodes.", StringComparison.Ordinal)).ToList();
        Assert.Contains(files, f => f.EndsWith(Path.Combine("PoolWatch.Launcher", "Program.cs"), StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("PoolWatch", "Program.cs"), StringComparison.Ordinal));
        var literal = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => !x.l.TrimStart().StartsWith("//", StringComparison.Ordinal) && LiteralExit.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.l.Trim()}").ToList();
        Assert.True(literal.Count == 0, "exit codes written as numbers: " + string.Join(Environment.NewLine, literal));
        Assert.All(["return 9;", "if (x) return 0;", "=> 1;", "Environment.Exit(2);", "Environment.ExitCode = 7;", "return -1;"], l => Assert.Matches(LiteralExit, l));
        Assert.All(["return ExitCodes.Done;", "return Engine.ExitCodes.NamesNotSafe;", "return count;", "x = 0;"], l => Assert.DoesNotMatch(LiteralExit, l));
    }

    private static readonly Regex LiteralExit = new(@"\breturn\s+-?\d+\s*;|=>\s*-?\d+\s*;|Environment\.Exit\(\s*-?\d+\s*\)|\bExitCode\s*=\s*-?\d+\s*;");

    /// <summary>
    /// docs/DESIGN.md's uninstaller paragraph says what each failure of the cleanup means: every code the cleanup can end
    /// with but the three that let the uninstaller go on, and the uninstaller's own -1 and -2, each in its list of failures
    /// ("N, ..."), before "any code the uninstaller does not know".
    /// </summary>
    [Fact]
    public void TheDesignSaysWhatEachFailureOfTheCleanupMeans()
    {
        string doc = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "docs", "DESIGN.md")));
        int from = doc.IndexOf("Any other exit is a failure", StringComparison.Ordinal);
        int to = doc.IndexOf("or any code the uninstaller does not know", from + 1, StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "docs/DESIGN.md has no list of the cleanup's failures");
        string failures = doc[from..to];
        var table = Table();
        var codes = FromCleanup.Except(CleanupDone).Where(n => n != nameof(ExitCodes.Done)).Select(n => table[n]).Append(-1).Append(-2).ToList();
        foreach (int c in codes)
            Assert.True(Regex.IsMatch(failures, $@"(?:: |; |, and ){c}, "), $"docs/DESIGN.md does not say what the cleanup's {c} means");
        Assert.Single(Regex.Matches(doc, "Any other exit is a failure"));
    }

    /// <summary>docs/TECHNICAL.md lists every code of the table, by its value and name, in one table, and only those.</summary>
    [Fact]
    public void TheDocsListEveryCodeInOneTable()
    {
        string doc = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "docs", "TECHNICAL.md")).Replace("\r\n", "\n");
        int start = doc.IndexOf("\n### Exit codes\n", StringComparison.Ordinal);
        Assert.True(start > 0, "docs/TECHNICAL.md has no Exit codes section");
        int end = doc.IndexOf("\n#", start + 2, StringComparison.Ordinal);
        string section = doc[start..(end < 0 ? doc.Length : end)];
        var rows = Regex.Matches(section, @"(?m)^\| (-?\d+) \| `(\w+)` \|").Select(m => (Name: m.Groups[2].Value, Value: int.Parse(m.Groups[1].Value))).ToList();
        Assert.Equal(Table().OrderBy(c => c.Value).Select(c => (c.Key, c.Value)), rows);
        Assert.Single(Regex.Matches(doc, @"(?m)^### Exit codes$"));
    }
}
