using System.Reflection;
using System.Text.RegularExpressions;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// Which opener each site that PoolWatch routes through the no-wait opener (<see cref="NoWaitOpen"/>) uses, read in its
/// source: each site's code calls the opener, or passes it on, and holds none of the plain opens it replaced (a plain
/// FileStream, File.ReadAllText, File.WriteAllText, File.AppendAllText or File.Open), and a replace checks the old file
/// right before its rename. A change that sets a site back to the plain open as it is usually written turns that site's
/// test red. These read the code's text as it is written (its comments and what its strings hold left out); what the
/// opener does is held by <see cref="NoWaitOpenTests"/>, and what a site does with a refused open by that site's tests.
/// </summary>
public sealed class NoWaitOpenSiteTests
{
    private static string Text(string[] file) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), "src", .. file]));

    /// <summary>The code of <paramref name="file"/> on one line.</summary>
    private static string Code(params string[] file) => UninstallerTests.OneLine(ScriptCode.CSharp(Text(file)));

    /// <summary>The code of the one member of <paramref name="file"/> that starts <paramref name="header"/>, on one line.</summary>
    private static string Member(string[] file, string header) =>
        UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(Text(file), header).Value));

    private static readonly Regex PlainOpen = new(@"\bnew FileStream\(|\bFile\.(ReadAllText|WriteAllText|AppendAllText|Open|OpenRead|OpenWrite|Create)\(|\bFileInfo\([^)]*\)\.Create\(");

    private static readonly string[] Engine = ["PoolWatch", "Engine"], Analysis = ["PoolWatch", "Analysis"];
    private static string[] In(string[] folder, string file) => [.. folder, file];

    [Fact]
    public void TheLogOpensEachLineAndEachNewFileThroughTheNoWaitOpener()
    {
        string log = Code(In(Engine, "Log.cs"));
        Assert.Contains("_open = open ?? NoWaitOpen.Open;", log);
        Assert.Contains("private FileStream Keep(string path, FileMode mode) => _open(path, mode, FileAccess.Write, FileShare.None, false);", log);
        Assert.Contains("private FileStream AppendOnly(string path) => _open(path, FileMode.OpenOrCreate, FileAccess.Write, Shared, true);", log);
        Assert.DoesNotMatch(PlainOpen, log);
        // The app's log is made with no opener of its own, so it has the default; and that default is the no-wait open.
        Assert.Contains("var log = new Log(LogFile, Guard.CheckReal, rollGate: _rollGate);", Member(In(Engine, "AppPaths.cs"), "public Log OpenLog()"));
        using var made = new Log(Path.Combine(Path.GetTempPath(), "pw-site-" + Guid.NewGuid().ToString("N") + ".log"));
        var open = (Delegate)typeof(Log).GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(made)!;
        Assert.Equal(typeof(NoWaitOpen).GetMethod(nameof(NoWaitOpen.Open)), open.Method);
    }

    [Fact]
    public void AReplaceWritesItsTemporaryFileThroughTheNoWaitOpenerAndChecksTheOldFileRightBeforeItsRename()
    {
        string replace = Member(In(Engine, "DurableFile.cs"), "public static void Replace(");
        Assert.Contains("open ??= NoWaitOpen.Open;", replace);
        Assert.Contains("failIfWait ??= NoWaitOpen.FailIfOpenWouldWait;", replace);
        Assert.Contains("using (var stream = open(tmp, FileMode.Create, FileAccess.Write, FileShare.None, false))", replace);
        Assert.Contains("failIfWait(guard.CheckReal(file)); File.Move(guard.CheckReal(tmp), guard.CheckReal(file), overwrite: true);", replace);
        Assert.DoesNotMatch(PlainOpen, replace);
        // The records the routed sites write whole go through it, each with its default opener and check.
        foreach (var (file, header, call) in new[]
                 {
                     (In(Engine, "AppPaths.cs"), "public RetentionClock OpenRetentionClock(", "DurableFile.Replace(Guard, RetentionStateFile, state.ToJson());"),
                     (In(Engine, "MonitorEngine.cs"), "private void WriteProfile(", "DurableFile.Replace(_paths.Guard, path, json);"),
                     (In(Analysis, "AnalysisPipeline.cs"), "private void WriteAtomic(", "DurableFile.Replace(_engine.Paths.Guard, path, content);"),
                     (In(Engine, "TokenLeakGuard.cs"), "private bool SaveState(", "DurableFile.Replace(_paths.Guard, StateFile, s.ToJson());"),
                 })
        {
            string member = Member(file, header);
            Assert.Contains(call, member);
            Assert.DoesNotMatch(PlainOpen, member);
        }
    }

    [Fact]
    public void TheCsvFilesAreOpenedThroughTheNoWaitOpener()
    {
        Assert.Contains("_open = open ?? NoWaitOpen.OpenUnbuffered;",
            Member(In(Engine, "CsvSink.cs"), "public CsvSink(AppPaths paths, Settings settings, bool verbose, Action<string>? log = null,"));
        // The unbuffered open is the no-wait open, with no buffer in its stream.
        string opener = Code(In(Engine, "NoWaitOpen.cs"));
        Assert.Contains("public static FileStream OpenUnbuffered(string path, FileMode mode, FileAccess access, FileShare share) => " +
                        "OpenStream(path, mode, access, share, appendOnly: false, bufferSize: 0);", opener);
        Assert.Contains("public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, bool appendOnly = false) => " +
                        "OpenStream(path, mode, access, share, appendOnly, bufferSize: 4096);", opener);
        string sink = Code(In(Engine, "CsvSink.cs"));
        Assert.Single(Regex.Matches(sink, @"\bnew CsvFile\("));
        Assert.Contains("file.File = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, file.Name)), file.Header, _open, file.From);", sink);
        Assert.DoesNotMatch(PlainOpen, sink);
        // The engine's sink is made with no opener of its own.
        Assert.Contains("_sink = new CsvSink(paths, settings, verboseCsv, log.Info);",
            Member(In(Engine, "MonitorEngine.cs"), "public MonitorEngine(Settings settings, AppPaths paths, Log log, bool verboseCsv)"));
    }

    [Fact]
    public void TheReportWritersPathCheckGivesUpAnOpenThatWouldWait()
    {
        Assert.Contains("string real = Guard.CheckReal(path); NoWaitOpen.FailIfOpenWouldWait(real); return real;",
            Member(In(Engine, "AppPaths.cs"), "public string CheckRealNoWait(string path)"));
        Assert.Contains("public string CheckWrite(string path) => _engine.Paths.CheckRealNoWait(path);", Code(In(Analysis, "WindowsIncidentHost.cs")));
        string diagnostics = Code(In(Analysis, "Diagnostics.cs"));
        int writes = Regex.Matches(diagnostics, @"\bFindingReport\.Write\(").Count;
        Assert.Equal(3, writes); // the crash and the two experiment reports; a spawn storm's folder is written by StormReports
        Assert.Equal(writes, Regex.Matches(diagnostics, @"\bFindingReport\.Write\([^;]*, _engine\.Paths\.CheckRealNoWait\);").Count);
        Assert.Contains("_markInbox ??= new Core.Experiments.MarkInbox(_engine.Paths.RequestsDir, _engine.Paths.CheckRealNoWait);", diagnostics);
        // The storm folders' writer is given the same check, and every path it makes, replaces or appends to goes through it.
        Assert.Single(Regex.Matches(diagnostics, @"\bStormReports\.Write"));
        Assert.Contains("StormReports.WriteWithin(_analysis.Incidents.Budget, Spawns, folder, _engine.Paths.IncidentsDir, _engine.Meter.OwnCostLine(), " +
                        "_engine.Paths.CheckRealNoWait, nowMs);", diagnostics);
        string storms = Code(["PoolWatch.Core", "Spawn", "StormReports.cs"]);
        Assert.Contains("Directory.CreateDirectory(checkWrite(f.Dir));", storms);
        Assert.Contains("bytes += FindingReport.Write(f.Dir, head, tracker.StormMarkdown(f) + \"\" + costLine + \"\", null, checkWrite);", storms); // strings read empty
        // A new spawns.csv is replaced in one step by the finding reports' own replace, which checks both of its paths.
        Assert.Contains("long written = FindingReport.Atomic(path, SpawnTracker.TraceHeader + \"\" + SpawnTracker.TraceRows(rows), checkWrite);", storms);
        Assert.Contains("using (var fs = new FileStream(checkWrite(path), FileMode.Append, FileAccess.Write, FileShare.Read))", storms);
        Assert.Single(Regex.Matches(storms, @"\bnew FileStream\(|\bFile\.(WriteAllText|WriteAllBytes|AppendAllText|Open|OpenWrite|Create)\(")); // that one
        string atomic = Member(["PoolWatch.Core", "Findings", "FindingLog.cs"], "internal static long Atomic(");
        Assert.Contains("string tmp = checkWrite(path + \"\"); File.WriteAllBytes(tmp, bytes); File.Move(tmp, checkWrite(path), overwrite: true);", atomic);
        Assert.Single(Regex.Matches(atomic, @"\bnew FileStream\(|\bFile\.(WriteAllText|WriteAllBytes|AppendAllText|Open|OpenWrite|Create)\(")); // that one, to the checked temp file
    }

    [Fact]
    public void TheRecordsReadAndWrittenWholeGoThroughTheNoWaitOpener()
    {
        foreach (var (file, header, calls) in new[]
                 {
                     (In(Analysis, "Diagnostics.cs"), "private void CheckVersions(",
                         new[] { "NoWaitOpen.ReadAllText(versionsFile)", "NoWaitOpen.WriteAllText(_engine.Paths.Guard.CheckReal(versionsFile), " }),
                     (In(Analysis, "Diagnostics.cs"), "private bool CheckCrashes(",
                         new[] { "NoWaitOpen.ReadAllText(seenFile)", "NoWaitOpen.WriteAllText(_engine.Paths.Guard.CheckReal(seenFile), _crashCursor.Format())" }),
                     (In(Analysis, "Diagnostics.cs"), "private void OnFinding(",
                         new[] { "NoWaitOpen.AppendAllText(_engine.Paths.Guard.CheckReal(Path.Combine(dir, \"\")), line);" }),
                     (In(Engine, "AppPaths.cs"), "public RetentionClock OpenRetentionClock(", new[] { "RetentionClock.State.Parse(NoWaitOpen.ReadAllText(file))" }),
                     (In(Engine, "TokenLeakGuard.cs"), "private GuardState? LoadState()", new[] { "GuardState.Parse(NoWaitOpen.ReadAllText(StateFile))" }),
                 })
        {
            string member = Member(file, header);
            Assert.All(calls, call => Assert.Contains(call, member));
            Assert.DoesNotMatch(PlainOpen, member);
        }
    }

    /// <summary>The rule finds the plain opens it names, as they are usually written, and passes the routed ones.</summary>
    [Fact]
    public void ThePlainOpenRuleFindsThePlainOpens()
    {
        foreach (var plain in new[] { "new FileStream(p, m, a, s)", "File.ReadAllText(file)", "File.WriteAllText(p, t)", "File.AppendAllText(p, t)",
                     "File.Open(p, m)", "new FileInfo(path).Create(m, r, s, 4096, o, null)" })
            Assert.Matches(PlainOpen, plain);
        foreach (var routed in new[] { "NoWaitOpen.ReadAllText(file)", "PoolWatch.Engine.NoWaitOpen.WriteAllText(p, t)", "NoWaitOpen.Open(p, m, a, s)",
                     "File.Move(a, b, overwrite: true)", "File.Exists(p)" })
            Assert.DoesNotMatch(PlainOpen, routed);
    }

    /// <summary>
    /// The texts that say what PoolWatch does with an open that would have to wait for another program, or with a folder
    /// that cannot be listed, say it in these words, each read as a sentence of its text (comment markers and line breaks
    /// left out): the opener's summary and its check before a rename, the log's, SECURITY.md's log paragraph, the report
    /// writers' path check, the replace's check, the CSV sink's and the CSV file's openers, the incident host's path
    /// check, and retention's summary and DESIGN.md on a folder that cannot be listed. A text whose sentence is changed
    /// turns this red. SECURITY.md's log paragraph says what is done when an append or a rename is refused, and the
    /// opener's, the replace's, the report writers' and the incident host's checks say what they do; none of these texts
    /// has the words, listed here, that said more (that the log always keeps being written, that a program that opens a
    /// new file at most moves the run on, that a file not rolled over grows until any hold ends, or that a check, a
    /// replace, a rename or a delete gives up or is given up).
    /// </summary>
    [Fact]
    public void TheTextsSayWhatPoolWatchDoesWithAnOpenThatWouldHaveToWait()
    {
        static string Prose(params string[] path) =>
            UninstallerTests.OneLine(Regex.Replace(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])), @"(?m)^\s*///?\s?", " "));
        foreach (var (path, sentence) in new (string[], string)[]
                 {
                     (["src", "PoolWatch", "Engine", "NoWaitOpen.cs"], "an open that would have to wait for another program before it could go ahead is given up at once, and is reported as refused"),
                     (["src", "PoolWatch", "Engine", "Log.cs"], "or an open that would have to wait for another program, which is given up at once — is tried again three times"),
                     (["SECURITY.md"], "or whose open would have to wait for another program (PoolWatch gives such an open up at once rather than wait) is tried again three times"),
                     (["SECURITY.md"], "- **Its log goes on in a new file when its appends stay refused.** Your account can read `poolwatch.log`"),
                     (["SECURITY.md"], "a program that opens it in that moment, or makes the next one first and keeps it open, in a folder it may write, refuses " +
                                       "its appends while it has it open: the run then moves on to another new file, with no line lost"),
                     (["SECURITY.md"], "or keeps the log's or a new file's `.1` open in a way that refuses the rename, that file is not renamed but can still be " +
                                       "appended to: it is not rolled over and grows until the program lets go"),
                     (["src", "PoolWatch", "Engine", "DurableFile.cs"], "When it throws, the rename is not made and the old file is as it was, exactly as when the temporary file could not be written."),
                     (["src", "PoolWatch", "Engine", "AppPaths.cs"], "when an open of that path would have to wait for another program at the time of the check."),
                     (["src", "PoolWatch", "Engine", "AppPaths.cs"], "In a folder that cannot be listed (when Windows refuses the listing, say), each thing the clock has a record of"),
                     (["docs", "DESIGN.md"], "In a folder that cannot be listed (when Windows refuses the listing, say), each thing with a record is still judged"),
                     (["src", "PoolWatch", "Engine", "CsvSink.cs"], "an open that would have to wait for another program is given up and refused"),
                     (["src", "PoolWatch", "Engine", "DurableFile.cs"], "it throws when an open of the old file would have to wait for another program at the time of the check"),
                     (["src", "PoolWatch.Core", "Util", "Csv.cs"], "The Windows app stands in one that never waits for another program (an open that would have to wait is given up and refused)."),
                     (["src", "PoolWatch", "Analysis", "WindowsIncidentHost.cs"], "The path check, run on each file before it is written or renamed, throws when an open " +
                                                                                 "of that file would have to wait for another program at the time of the check"),
                     (["src", "PoolWatch", "Engine", "NoWaitOpen.cs"], "is given up at once, and is reported as refused — the <see cref=\"IOException\"/> a sharing refusal raises (<c>ERROR_SHARING_VIOLATION</c>)"),
                     (["src", "PoolWatch", "Engine", "NoWaitOpen.cs"], "The handle is open when the open succeeded or was given up (it is then closed unused, with no I/O on it), and invalid otherwise."),
                     (["src", "PoolWatch", "Engine", "NoWaitOpen.cs"], "Throws the refusal <see cref=\"Open\"/> throws when an open of <paramref name=\"path\"/> would " +
                                                                       "have to wait for another program, and returns quietly otherwise."),
                 })
            Assert.Contains(sentence, Prose(path));
        foreach (var (path, said) in new (string[], string)[]
                 {
                     (["SECURITY.md"], "Its log keeps being written"),
                     (["SECURITY.md"], "at most moves the run on"),
                     (["SECURITY.md"], "grows until the hold ends"),
                     (["src", "PoolWatch", "Engine", "DurableFile.cs"], "A replace that is given up"),
                     (["src", "PoolWatch", "Analysis", "WindowsIncidentHost.cs"], "gives up a write or rename"),
                     (["src", "PoolWatch", "Engine", "AppPaths.cs"], "renamed or deleted, but which also gives up"),
                     (["src", "PoolWatch", "Engine", "NoWaitOpen.cs"], "Gives up (throws the refusal"),
                 })
            Assert.DoesNotContain(said, Prose(path));
    }

    /// <summary>
    /// Outside its interop declarations the opener's code names what it asks Windows for and what it gets back in its own
    /// words: the result of an open that was given up, at both places that read it, and the option that asks for it, in
    /// the one open.
    /// </summary>
    [Fact]
    public void TheOpenersCodeUsesItsOwnNamesOutsideItsInteropDeclarations()
    {
        string text = Text(In(Engine, "NoWaitOpen.cs"));
        string code = UninstallerTests.OneLine(ScriptCode.CSharp(text[..text.IndexOf("// --- interop ---", StringComparison.Ordinal)]));
        Assert.Equal(2, Regex.Matches(code, Regex.Escape("if (status == StatusGivenUp) throw Refused(path);")).Count);
        Assert.Contains("uint options = FILE_SYNCHRONOUS_IO_NONALERT | GiveUpRatherThanWait | extraOptions;", code);
    }

    /// <summary>
    /// The same holds for the helper with which <see cref="NoWaitOpenTests"/> keeps its own temp file: outside its
    /// interop declarations at the foot of that file, its code names the request it makes and that request's input in its
    /// own words (AskToHold, HoldInput, Level), and not by the Windows names those declarations give them (read from the
    /// declarations; the code is read with its comments and what its strings hold left out).
    /// </summary>
    [Fact]
    public void TheTestsHoldHelperUsesItsOwnNamesOutsideItsInteropDeclarations()
    {
        string text = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "tests", "PoolWatch.AppTests", "NoWaitOpenTests.cs"));
        int interop = text.IndexOf("// --- interop ---", StringComparison.Ordinal);
        Assert.True(interop > 0, "the file has its interop marker");
        string code = UninstallerTests.OneLine(ScriptCode.CSharp(text[..interop]));
        Assert.Contains("var input = new HoldInput { StructureVersion = 1, StructureLength = (ushort)Marshal.SizeOf<HoldInput>(), Level = level,", code);
        Assert.Contains("bool ok = DeviceIoControl(handle, AskToHold, inBuf, input.StructureLength, outBuf, 64, out _, ov);", code);
        string declared = UninstallerTests.OneLine(text[interop..]);
        var input = Regex.Match(declared, @"Windows' (\w+) \(HoldInput in the code; Level is its (\w+)\)");
        string[] windowsNames = [Regex.Match(declared, @"\b(\w+) = 0x[0-9A-Fa-f]+, AskToHold = \1;").Groups[1].Value, input.Groups[1].Value, input.Groups[2].Value];
        Assert.All(windowsNames, name => Assert.NotEqual("", name));
        Assert.All(windowsNames, name => Assert.DoesNotMatch($@"\b{name}\b", code));
    }
}
