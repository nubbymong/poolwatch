extern alias launcher;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Changes;
using PoolWatch.Engine.Changes;

namespace PoolWatch.AppTests;

/// <summary>
/// The uninstaller's side of putting PoolWatch's changes back (installer/PoolWatch.iss): by default it puts back each
/// change, or says why it could not, and keeps them only when told to; a silent uninstall and /DELETEDATA never ask;
/// started by hand it first says what it puts back, read from PoolWatch's own list (<c>--changes-dump</c>, <see
/// cref="ChangeLedger.ToJson"/>), line by line; and afterwards it says what it put back, read from
/// uninstall-summary.txt (<see cref="Program.UninstallSummary"/>). These tests hold the script's choices, its file
/// handling and the shapes of the two files it reads, with a reader written the way the script's is. The script itself
/// is first compiled and run by CI (the installer build, and its end-to-end test, which uninstalls silently only); the
/// reader here is a copy of the script's, held to the script's own text (<see cref="TheReaderHereIsTheScriptsOwn"/>):
/// change the two together. The same goes for the script's reading of a helper's command line, copied here and held to
/// PoolWatch's own (<see cref="ScriptIsHelperLine"/>). Every routine of the script's [Code] is held to its whole text
/// as written, its comments included, with every run of white space made one space (<see
/// cref="EveryRoutineOfTheScriptIsHeldWhole"/> fails when one is not), so an edit to a routine fails a test here unless
/// it only changes how long a run of white space is, or which white space it is. Outside its routines [Code] holds only
/// comments, none of them a compiler directive, and plain declarations of constants and variables (<see
/// cref="OutsideItsRoutinesTheCodeHasOnlyPlainConstantsAndVariables"/>), which are held only where a test reads them,
/// and Inno Setup's preprocessor passes it on as it is (<see cref="ThePreprocessorPutsNothingIntoTheCode"/>). The
/// script's lines are split as Inno Setup reads the file (<see cref="IssLines"/>: a line ends at a CR LF, a CR or an
/// LF), the white space at their ends is taken as its compiler and its preprocessor take it (<see cref="IsccTrim"/>,
/// <see cref="IsDirectiveLine"/>, <see cref="IsPreprocessorComment"/>), and no line of the script longer than two
/// characters may end in white space (a character up to #32) and a backslash, which its preprocessor joins to the next
/// line (<see cref="JoinsTheNextLine"/>, held by <see cref="CodeSectionOf"/>), so it joins no two of them (a line of
/// two characters or fewer it does not join); [Code]'s comments and strings are read as Pascal Script reads them (<see
/// cref="ScriptCode.Pascal"/>). The script itself is compiled by CI, and its installer test runs it.
/// </summary>
public sealed class UninstallerTests
{
    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return File.ReadAllText(Path.Combine(dir.FullName, "installer", "PoolWatch.iss"));
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The script's [Code] section, as written (<see cref="CodeSectionOf"/>).</summary>
    internal static string CodeSection() => CodeSectionOf(Script());

    /// <summary>
    /// The [Code] section of a setup script, as written: from its header line to the end of the script (the section is the
    /// script's last: <see cref="InstallerPagesTests.NoLineOfTheCodeBeginsWithABracketOrAHash"/>). A header of it is what
    /// Inno Setup's compiler takes for one: a line (<see cref="IssLines"/>) that is <c>[Code]</c>, in any case, once the
    /// characters up to #32 at its ends are taken off (<see cref="IsccTrim"/>). No line of the script may be one that
    /// its preprocessor joins to the next (<see cref="JoinsTheNextLine"/>), or this fails, so no line the compiler reads
    /// is two of the script's lines made one (what else the preprocessor does to [Code] is held by
    /// <see cref="ThePreprocessorPutsNothingIntoTheCode"/>). The script has one header, written <c>[Code]</c> with
    /// nothing else on its line, or this fails: so a mention of the section anywhere else, such as in a comment of an
    /// earlier section, is not its start, and a second header, which would start a second section of code no test
    /// reads, fails.
    /// </summary>
    internal static string CodeSectionOf(string script)
    {
        var lines = IssLines(script);
        var joined = lines.Select((l, i) => (Number: i + 1, l.Text)).Where(l => JoinsTheNextLine(l.Text)).ToList();
        Assert.True(joined.Count == 0, "lines of the script that Inno Setup's preprocessor joins to the next (longer than two characters, they " +
            "end in white space and a backslash): " + string.Join(", ", joined.Select(l => $"line {l.Number}: \"{l.Text}\"")));
        var headers = lines.Where(l => IsccTrim(l.Text).Equals("[Code]", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(headers.Count == 1, $"the script has {headers.Count} [Code] header lines, not one");
        Assert.True(headers[0].Text == "[Code]", $"the [Code] header is not written [Code] with nothing else on its line: \"{headers[0].Text}\"");
        return script[headers[0].Start..];
    }

    /// <summary>
    /// The lines of a setup script as Inno Setup reads the file (its compiler's TTextFileReader, which also gives its
    /// preprocessor the script's lines): each line ends at a CR LF, a CR or an LF, which is not in its text; with where it
    /// starts in the script. Of the setup script's lines, the preprocessor joins none to the next
    /// (<see cref="CodeSectionOf"/> fails on one it would join).
    /// </summary>
    internal static List<(int Start, string Text)> IssLines(string script)
    {
        var lines = new List<(int Start, string Text)>();
        for (int start = 0, i = 0; start < script.Length; i++)
        {
            if (i < script.Length && script[i] is not ('\r' or '\n')) continue;
            lines.Add((start, script[start..i]));
            if (i + 1 < script.Length && script[i] == '\r' && script[i + 1] == '\n') i++;
            start = i + 1;
        }
        return lines;
    }

    /// <summary>The characters up to #32: a space and every character before it (#0 to #31, the tab among them).</summary>
    private static readonly char[] UpToSpace = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    /// <summary>
    /// A line without the characters up to #32 at its ends, as Delphi's Trim takes them off; Inno Setup's compiler reads
    /// a line so to tell whether it is a section's header (any line whose text so read starts with <c>[</c>).
    /// </summary>
    internal static string IsccTrim(string line) => line.Trim(UpToSpace);

    /// <summary>
    /// Whether Inno Setup's preprocessor (ISPP) reads the line as one of its directives (and so does not pass the line on
    /// as it is): its first character other than #1 to #32 is <c>#</c>.
    /// </summary>
    internal static bool IsDirectiveLine(string line) => Regex.IsMatch(line, @"^[\x01-\x20]*#");

    /// <summary>
    /// Whether ISPP takes the line out of the script as a comment of its own: its first characters other than #1 to #32
    /// are <c>//</c>.
    /// </summary>
    internal static bool IsPreprocessorComment(string line) => Regex.IsMatch(line, @"^[\x01-\x20]*//");

    /// <summary>
    /// Whether ISPP joins the line and the next into one line before Inno Setup's compiler reads them: the line is
    /// longer than two characters and ends in a backslash with a character up to #32 just before it. That is ISPP's rule
    /// for a line that ends in its span symbol; the symbol is #0 until a <c>#pragma spansymbol</c> sets it, and the
    /// ISPPBuiltins.iss that ISPP reads in before every script when it is in ISCC's folder (Inno Setup's own setup puts
    /// it there) sets it to a backslash, for the script too.
    /// </summary>
    internal static bool JoinsTheNextLine(string line) => line.Length > 2 && line[^1] == '\\' && line[^2] <= ' ';

    /// <summary>
    /// The script's [Code] without its comments (<see cref="ScriptCode.Pascal"/>: each a bare <c>{}</c>), with every run of
    /// white space made one space: read to count what is in the code and to find a text in it, so a copy in a comment is
    /// neither counted nor found. Finding a text holds that it is there, not where.
    /// </summary>
    private static string Code() => OneLine(ScriptCode.Pascal(CodeSection()));

    /// <summary>
    /// One function or procedure of the script, from its header to its own last <c>end;</c>, read as <see cref="Code"/>
    /// reads the script (comments a bare <c>{}</c>, white space one space, in strings too): the only one of its name in the
    /// code, never a copy in a comment. The tests read it to count what is in it (a call, a box) and to find a text in it,
    /// which holds that the text is there, not where; a statement is held where it is with <see cref="BlocksAround"/> or
    /// <see cref="Block"/>, and each routine is held whole with <see cref="Whole"/>.
    /// </summary>
    private static string Body(string name) => OneLine(ScriptCode.PascalRoutine(ScriptCode.Pascal(CodeSection()), name));

    /// <summary>
    /// The heads of the blocks around <paramref name="statements"/> (one whole statement, or several consecutive ones) of the
    /// routine <paramref name="routine"/> (<see cref="ScriptCode.PascalBlocksAround"/>): a pin that holds them holds the
    /// statements as Pascal tokens (strings as written, so not a copy in a string), their order, and the condition of every
    /// block they are in, so an edited statement, a changed condition around it, or a new block around it fails the pin. A
    /// statement given with a block in it shown as <c>begin ... end</c> is held without what that block holds: that pin
    /// passes whatever the block holds, so the statements of each such block are held by pins of their own (here
    /// <see cref="Block"/>, which holds all that a block holds, or a pin of those statements).
    /// </summary>
    internal static IReadOnlyList<string> BlocksAround(string routine, string statements) => ScriptCode.PascalBlocksAround(CodeSection(), routine, statements);

    /// <summary>
    /// The statements of one block of the routine <paramref name="routine"/>, in order, each read as
    /// <see cref="BlocksAround"/> reads it, in full: the statements whose heads are <paramref name="heads"/> (none: the
    /// routine's own body). A pin that holds them equal to a list holds all that the block holds.
    /// </summary>
    internal static IReadOnlyList<string> Block(string routine, params string[] heads) =>
        ScriptCode.PascalStatements(CodeSection(), routine).Where(s => s.Heads.SequenceEqual(heads)).Select(s => s.Statement).ToList();

    /// <summary>The heads of ReadChangeList's loop over the list's lines, and of its branch for a row's end with a name read.</summary>
    private const string ForEachLine = "for I := 0 to GetArrayLength(Lines) - 1 do",
        AtARowsEnd = "if Line = '  {' then begin ... end else if Copy(Line, 1, Length(WhatKey)) = WhatKey then What := JsonText(Line, Length(WhatKey) + 1) " +
            "else if Copy(Line, 1, Length(LeavesKey)) = LeavesKey then Leaves := JsonText(Line, Length(LeavesKey) + 1) " +
            "else if Copy(Line, 1, Length(CaveatKey)) = CaveatKey then Caveat := JsonText(Line, Length(CaveatKey) + 1) " +
            "else if ((Line = '  }') or (Line = '  },')) and (What <> '') then";

    /// <summary>A string constant of the script's code (a Pascal string, with no quote inside), set there once.</summary>
    private static string Constant(string name) =>
        Assert.Single(Regex.Matches(ScriptCode.Pascal(CodeSection()), $@"(?m)^\s*{name} = '([^']*)';")).Groups[1].Value;

    private static int Number(string name) => int.Parse(Assert.Single(Regex.Matches(ScriptCode.Pascal(CodeSection()), $@"(?m)^\s*{name} = (\d+);")).Groups[1].Value);

    /// <summary>
    /// One routine of the script whole, as written (its comments too), from its header to its own last <c>end;</c> (one
    /// Windows provides: to the semicolon after its <c>external '...'</c>), with every run of white space made one space
    /// (<see cref="ScriptCode.PascalRoutine"/>): the only one of its name in the code, so a copy in a comment before it is
    /// not taken for it. A pin that holds it equal to a text holds every line, condition and comment in it: an edit that
    /// only changes how long a run of white space is, or which white space it is, leaves the pin green; any other edit
    /// fails it. Every routine of [Code] has such a pin in this file (<see cref="EveryRoutineOfTheScriptIsHeldWhole"/>).
    /// </summary>
    private static string Whole(string name) => OneLine(ScriptCode.PascalRoutine(CodeSection(), name));

    /// <summary>A text with every run of white space made one space, as <see cref="Body"/> reads the script, and none at either end.</summary>
    internal static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// Pascal source with each pair of string literals joined by <c>+</c> made one literal (<c>'a ' + 'b'</c> reads
    /// <c>'a b'</c>), so a text written over several literals, as the script writes a long message, reads as it is shown.
    /// </summary>
    internal static string JoinedLiterals(string pascal) => Regex.Replace(pascal, @"'\s*\+\s*'", "");

    /// <summary>
    /// The start of each line of the text a routine builds in Result (read with <see cref="JoinedLiterals"/>): the literal
    /// it is set to (<c>Result := '...'</c>), and each literal, or format string, after a line break (<c>#13#10 + '...'</c>,
    /// <c>#13#10 + Format('...'</c>).
    /// </summary>
    internal static List<string> LineStarts(string routine) =>
        [.. Regex.Matches(JoinedLiterals(routine), @"(?:Result := |#13#10 \+ (?:Format\()?)'((?:[^']|'')*)'").Select(m => m.Groups[1].Value)];

    /// <summary>What the uninstaller shows of the list: the changes it puts back, the ones it leaves with why, and what stays for a while after.</summary>
    private sealed record Shown(List<string> PutsBack, List<string> Leaves, List<string> Caveats);

    /// <summary>The script's Shorten.</summary>
    private static string Shorten(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;

    /// <summary>
    /// What the uninstaller makes of the list, as ReadChangeList, JsonText and AddShown in the script do: a row starts at
    /// a line "  {" and ends at "  }" or "  },"; its "what", "uninstallLeaves" and "putBackCaveat" lines give its words;
    /// a row with no uninstallLeaves is one it puts back. A name shown is cut to MaxNameShown characters and a line to
    /// MaxLineShown; each caveat is shown once.
    /// </summary>
    private static Shown ReadAsTheUninstallerDoes(string json)
    {
        string whatKey = Constant("WhatKey"), leavesKey = Constant("LeavesKey"), caveatKey = Constant("CaveatKey");
        int maxName = Number("MaxNameShown"), maxLine = Number("MaxLineShown");
        var shown = new Shown([], [], []);
        string what = "", why = "", caveat = "";
        foreach (var raw in json.Split('\n'))
        {
            string line = raw.TrimEnd(); // Pascal's TrimRight: spaces and control characters at the end
            if (line == "  {") { what = ""; why = ""; caveat = ""; }
            else if (line.StartsWith(whatKey, StringComparison.Ordinal)) what = JsonText(line, whatKey.Length);
            else if (line.StartsWith(leavesKey, StringComparison.Ordinal)) why = JsonText(line, leavesKey.Length);
            else if (line.StartsWith(caveatKey, StringComparison.Ordinal)) caveat = JsonText(line, caveatKey.Length);
            else if (line is "  }" or "  }," && what.Length > 0)
            {
                if (why.Length == 0)
                {
                    shown.PutsBack.Add(Shorten(Shorten(what, maxName), maxLine));
                    if (caveat.Length > 0 && !shown.Caveats.Contains(Shorten(caveat, maxLine))) shown.Caveats.Add(Shorten(caveat, maxLine));
                }
                else shown.Leaves.Add(Shorten(Shorten(what, maxName) + ". " + why, maxLine));
                what = why = caveat = "";
            }
        }
        return shown;
    }

    private static string JsonText(string s, int p)
    {
        var text = new StringBuilder();
        for (; p < s.Length; p++)
        {
            char c = s[p];
            if (c == '"') return text.ToString();
            if (c != '\\') { text.Append(c); continue; }
            if (++p >= s.Length) return text.ToString();
            c = s[p];
            if (c == 'u') { text.Append(' '); p += 4; }
            else if ("bfnrt".Contains(c)) text.Append(' ');
            else text.Append(c);
        }
        return text.ToString();
    }

    private static ActiveChange Row(string id, ChangeKind kind, string what, bool canUndo = true, string? note = null) => new(id, kind, DateTime.UtcNow, what, canUndo, note);

    /// <summary>
    /// The guard's row; <paramref name="stays"/>: the guard found its setting already changed, so it stays after the guard is
    /// turned off (SettingsSource sets the caveat then).
    /// </summary>
    private static ActiveChange Guard(bool canUndo = true, bool stays = false) =>
        new(ChangeIds.TokenGuard, ChangeKind.TokenGuard, null, ChangeWords.TokenGuard, canUndo, ChangeWords.TokenGuardNote)
        {
            Title = ChangeWords.TokenGuardTitle, PutBackCaveat = stays ? ChangeWords.TokenGuardSettingStays : null,
        };

    // ---------------------------------------------------------------- the list it reads before it asks

    [Fact]
    public void TheUninstallerSaysEachChangeItPutsBackAndWhyItCannotPutBackAnother()
    {
        var service = Row("service:a", ChangeKind.Service, "Turned off Gerätedienst \"Beispiel\" (Example\\Svc)") with { Detail = "service · was Manual" };
        var tabbed = Row("task:a", ChangeKind.ScheduledTask, "Turned off the scheduled task \\Vendor\\Up\tdater");
        var logon = Row(ChangeIds.LogonTask, ChangeKind.LogonTask, ChangeWords.LogonTask);
        var held = Row("service:mapsbroker", ChangeKind.Service, "Turned off MapsBroker (profile Build box, at start)") with
        {
            Warning = ChangeWords.KeptOffAtStart("Build box", "MapsBroker"), AtStart = new AtStartHold("Build box", "MapsBroker"),
        };
        var warned = Row("clear-task:a", ChangeKind.RemovedClearTask, "Replaced your own clear task \"Clear standby\" (it ran RAMMap)") with { Warning = "This task runs RAMMap from C:\\Tools." };
        var cannot = Row("unread:applied-changes", ChangeKind.Shortcut, "PoolWatch could not read the shortcuts it changed", canUndo: false, note: ChangeWords.UnreadNote("Access is denied."));
        ActiveChange[] rows = [service, tabbed, logon, held, warned, cannot];

        string json = ChangeLedger.ToJson(rows);
        var shown = ReadAsTheUninstallerDoes(json);

        Assert.Equal([service.What, "Turned off the scheduled task \\Vendor\\Up dater", logon.What, held.What], shown.PutsBack);
        Assert.Equal([warned.What + ". " + ChangeWords.UninstallNeedsYourYes, cannot.What + ". " + cannot.Note], shown.Leaves);
        Assert.Empty(shown.Caveats);
        // What it lists is what PoolWatch itself says the uninstaller does with each row.
        Assert.Equal(rows.Where(r => r.UninstallLeaves is null).Count(), shown.PutsBack.Count);
        // Names are written as they are (not as \u escapes), so a name in another language reads as it is.
        Assert.Contains("Gerätedienst", json);
        Assert.Contains("· was Manual", json);
        // Still JSON, for CI and support.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(rows.Length, doc.RootElement.GetArrayLength());
        Assert.Equal(service.What, doc.RootElement[0].GetProperty("what").GetString());
    }

    [Fact]
    public void TheUninstallerSaysTheTokenLeakGuardsWindowsSettingStaysUntilSignOut()
    {
        // When the guard found its Windows setting already changed and has no record of changing it itself, the setting
        // lasts until you sign out after the guard is turned off: the uninstaller that turns it off says so, under its list
        // before it asks and in what it says once it put the changes back. The setting the guard changed itself, it puts back.
        Assert.Empty(ReadAsTheUninstallerDoes(ChangeLedger.ToJson([Guard()])).Caveats);
        Assert.DoesNotContain("putBackCaveat", ChangeLedger.ToJson([Guard()]));
        var shown = ReadAsTheUninstallerDoes(ChangeLedger.ToJson([Guard(stays: true), Row(ChangeIds.LogonTask, ChangeKind.LogonTask, ChangeWords.LogonTask)]));
        Assert.Equal([ChangeWords.TokenGuard, ChangeWords.LogonTask], shown.PutsBack);
        Assert.Equal([ChangeWords.TokenGuardSettingStays], shown.Caveats);
        Assert.Contains("    \"putBackCaveat\": \"The token-leak guard's Windows setting stays until you sign out.\"", ChangeLedger.ToJson([Guard(stays: true)]));
        // Only a change it puts back has one: the guard's row that could not be read has none, and no other kind does.
        Assert.Null(Guard(canUndo: false).PutBackCaveat);
        Assert.Empty(ReadAsTheUninstallerDoes(ChangeLedger.ToJson([Guard(canUndo: false)])).Caveats);
        Assert.All(Enum.GetValues<ChangeKind>().Where(k => k != ChangeKind.TokenGuard), k => Assert.Null(Row("x", k, "x").PutBackCaveat));

        // The question's caveats, right after the list it read, under that list.
        Assert.Equal(["if ListRead then"], BlocksAround("PutBackAnswer",
            "Text := 'These are the changes PoolWatch made on this PC that are still in effect. Uninstalling puts each one back ' + " +
            "'as it was before PoolWatch changed it:' + #13#10 + PutBackList + MoreLine(PutBackCount); " +
            "if CaveatList <> '' then Text := Text + #13#10 + CaveatList;"));
        Assert.Empty(BlocksAround("PutBackDone", "if (Count + PartCount <> 0) and (CaveatList <> '') then Result := Result + #13#10 + CaveatList " +
            "else if (Count + PartCount <> 0) and not ListRead then Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;"));
        // Logged for a row it puts back that has a caveat: the line and every condition of the blocks around it.
        Assert.Equal([ForEachLine, AtARowsEnd, "if Leaves = '' then", "if Caveat <> '' then"], BlocksAround("ReadChangeList", "Log('After it is put back: ' + Caveat);"));
        // Also when something could not be put back (an uninstall with /DELETEDATA asked nothing first), and, when PoolWatch
        // could not list its changes, as a line that says it only if the guard was on.
        Assert.Empty(BlocksAround("NotAllPutBack", "if CaveatList <> '' then Result := Result + #13#10 + CaveatList " +
            "else if not ListRead then Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;"));
        // All that the list's branch holds: the list, its caveats, and then what uninstalling cannot put back, if anything.
        Assert.Equal(["Text := 'These are the changes PoolWatch made on this PC that are still in effect. Uninstalling puts each one back ' + " +
            "'as it was before PoolWatch changed it:' + #13#10 + PutBackList + MoreLine(PutBackCount)",
            "if CaveatList <> '' then Text := Text + #13#10 + CaveatList",
            "if LeftCount > 0 then Text := Text + #13#10#13#10 + 'Uninstalling cannot put these back:' + #13#10 + LeftList + MoreLine(LeftCount)"],
            Block("PutBackAnswer", "if ListRead then"));
        // When no list was read: the else of the list's branch, after a blank line, as elsewhere.
        Assert.Empty(BlocksAround("PutBackAnswer", "if ListRead then begin ... end else Text := 'PoolWatch could not list its changes just now. " +
            "Uninstalling puts back each change it recorded, or says ' + 'why it could not.' + #13#10#13#10 + CaveatIfGuardWasOn;"));
        // Said of a guard that may have been on: the setting it changed itself is put back by the cleanup, so only one it found
        // already changed stays (the list's own words for that case).
        Assert.Equal("If the token-leak guard was on and found its Windows setting already changed, that setting stays until you sign out.", Constant("CaveatIfGuardWasOn"));
        Assert.Equal("The token-leak guard's Windows setting stays until you sign out.", ChangeWords.TokenGuardSettingStays);
    }

    [Fact]
    public void WithNothingToPutBackTheUninstallerListsNothing()
    {
        var shown = ReadAsTheUninstallerDoes(ChangeLedger.ToJson([]));
        Assert.Empty(shown.PutsBack);
        Assert.Empty(shown.Leaves);
        Assert.Empty(shown.Caveats);
    }

    [Fact]
    public void ALongNameIsCutInTheBoxesAndLoggedWhole()
    {
        // A startup app's name is chosen by whoever made the entry: a box names at most MaxNameShown characters of it,
        // and a line at most MaxLineShown, so the buttons stay on the screen.
        int maxName = Number("MaxNameShown"), maxLine = Number("MaxLineShown");
        Assert.Equal((120, 360), (maxName, maxLine));
        // The notices after the cleanup cut each summary line ("Put back: ...") to a name and some room for why.
        Assert.Equal(maxName + 60, Number("MaxSummaryShown"));
        string name = "Turned off the startup app " + new string('A', 1000);
        var shown = ReadAsTheUninstallerDoes(ChangeLedger.ToJson(
        [
            Row("startup:a", ChangeKind.StartupEntry, name),
            Row("unread:x", ChangeKind.Shortcut, name, canUndo: false, note: new string('B', 1000)),
        ]));
        Assert.Equal(name[..(maxName - 3)] + "...", Assert.Single(shown.PutsBack));
        Assert.Equal(maxLine, Assert.Single(shown.Leaves).Length);
        Assert.StartsWith(name[..(maxName - 3)] + "...", shown.Leaves[0]);

        // Logged whole and shown cut, each under its row's conditions: at a row's end with a name read, one with nothing it
        // leaves, or one it leaves.
        Assert.Equal([ForEachLine, AtARowsEnd, "if Leaves = '' then"], BlocksAround("ReadChangeList",
            "Log('Uninstalling puts back: ' + What); AddShown(PutBackList, PutBackCount, Shorten(What, MaxNameShown));"));
        Assert.Equal([ForEachLine, AtARowsEnd, "if Leaves = '' then begin ... end else"], BlocksAround("ReadChangeList",
            "Log('Uninstalling cannot put back: ' + What + '. ' + Leaves); AddShown(LeftList, LeftCount, Shorten(What, MaxNameShown) + '. ' + Leaves);"));
        // Each line counted, and shown cut while under the limit; and the cut itself.
        Assert.Equal(["Count := Count + 1", "if Count <= MaxShown then List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown)"], Block("AddShown"));
        Assert.Equal(["if Length(S) > Max then Result := Copy(S, 1, Max - 3) + '...' else Result := S"], Block("Shorten"));
    }

    [Fact]
    public void TheScriptReadsTheLinesTheListIsWrittenWith()
    {
        Assert.Equal("    \"what\": \"", Constant("WhatKey"));
        Assert.Equal("    \"uninstallLeaves\": \"", Constant("LeavesKey"));
        Assert.Equal("    \"putBackCaveat\": \"", Constant("CaveatKey"));
        Assert.Equal("uninstall-list.json", Constant("ListFileName"));
        string json = ChangeLedger.ToJson([Row("unread", ChangeKind.Shortcut, "x", canUndo: false, note: "why"), Guard(stays: true)]);
        var lines = json.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains("  {", lines);
        Assert.Contains("    \"what\": \"x\",", lines);
        Assert.Contains(lines, l => l.StartsWith("    \"uninstallLeaves\": \"why\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("    \"putBackCaveat\": \"", StringComparison.Ordinal));
        Assert.Contains("  },", lines);
        Assert.Contains("  }", lines);
        // The list comes from the installed PoolWatch (started through its launcher), into its own admin-only data folder,
        // and is deleted once read.
        string code = Code();
        Assert.Empty(BlocksAround("ReadChangeList", "ListFile := DataDir + '\\' + ListFileName; Launcher := LauncherPath(ExpandConstant('{app}')); " +
            "if (Launcher = '') or not Exec(Launcher, '--changes-dump \"' + ListFile + '\"', '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;"));
        Assert.Empty(BlocksAround("ReadChangeList", "ListRead := LoadStringsFromFile(ListFile, Lines); DeleteFile(ListFile);"));
    }

    [Fact]
    public void WhenPoolWatchMadeNoListTheUninstallerTouchesNothingInTheDataFolder()
    {
        // No list (PoolWatch could not start, or refused a data folder it could not make safe): that folder was not
        // secured this time, so the uninstaller neither reads nor deletes anything there; it says so in its log and goes on.
        // The failure branch is only a log line and Exit, and the list is read and deleted only after it: statements of the
        // routine's body, one after the other.
        Assert.Empty(BlocksAround("ReadChangeList", "if Code <> 0 then begin Log(Format('PoolWatch could not list its changes (exit %d).', [Code])); Exit; end; " +
            "ListRead := LoadStringsFromFile(ListFile, Lines); DeleteFile(ListFile);"));
        // The one file operation in the data folder: deleting the list it read, after PoolWatch made it (exit 0).
        string read = Body("ReadChangeList");
        Assert.Single(Regex.Matches(read, @"\bDeleteFile\("));
        Assert.Single(Regex.Matches(read, @"\bLoadStringsFromFile\("));
        foreach (var op in new[] { "DelTree(", "RenameFile(", "SaveStringToFile(", "SaveStringsToFile(", "FileCopy(", "RemoveDir(", "CreateDir(", "ForceDirectories(" })
            Assert.DoesNotContain(op, read);
    }

    [Fact]
    public void TheReaderHereIsTheScriptsOwn()
    {
        // The C# reader above copies these parts of the script: a change to either side shows here. Each statement is read
        // as Pascal tokens, its strings as written (the script's "  {" is two spaces and a brace).
        Assert.StartsWith("function JsonText(const S: String; P: Integer): String; var C: Char; begin ", Body("JsonText"));
        Assert.Equal(["Result := ''", "while P <= Length(S) do begin C := S[P]; if C = '\"' then Exit; if C = '\\' then begin P := P + 1; if P > Length(S) then Exit; C := S[P]; " +
            "if C = 'u' then begin Result := Result + ' '; P := P + 4; end " +
            "else if (C = 'b') or (C = 'f') or (C = 'n') or (C = 'r') or (C = 't') then Result := Result + ' ' " +
            "else Result := Result + C; end else Result := Result + C; P := P + 1; end"], Block("JsonText"));
        // In the loop over the list's lines: each line read, then a row's start, its three keys and its end, in that order.
        Assert.Equal([ForEachLine], BlocksAround("ReadChangeList", "Line := TrimRight(Lines[I]); " + AtARowsEnd + " begin ... end;"));
        Assert.Equal(["What := ''", "Leaves := ''", "Caveat := ''"], Block("ReadChangeList", ForEachLine, "if Line = '  {' then"));
        // At a row's end, what the row said is shown, and the row's words are cleared.
        Assert.Equal([ForEachLine, AtARowsEnd], BlocksAround("ReadChangeList", "if Leaves = '' then begin ... end else begin ... end; What := ''; Leaves := ''; Caveat := '';"));
        // A caveat only for a row it puts back that has one: logged whole, then cut and kept once.
        Assert.Equal(["Log('After it is put back: ' + Caveat)", "Shown := Shorten(Caveat, MaxLineShown)", "if Pos(Shown, CaveatList) = 0 then CaveatList := CaveatList + #13#10 + Shown"],
            Block("ReadChangeList", ForEachLine, AtARowsEnd, "if Leaves = '' then", "if Caveat <> '' then"));
        Assert.Equal(["Count := Count + 1", "if Count <= MaxShown then List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown)"], Block("AddShown"));
    }

    // ---------------------------------------------------------------- what it says after the cleanup

    [Fact]
    public void TheNoticesAfterTheCleanupReadTheLinesPoolWatchWrites()
    {
        // uninstall-summary.txt is PoolWatch's (Program.UninstallSummary); the script picks its lines by three prefixes.
        string putBack = Constant("PutBackPrefix"), partlyPutBack = Constant("PartlyPutBackPrefix"), notPutBack = Constant("NotPutBackPrefix");
        Assert.Equal(("Put back: ", "Partly put back: ", "Could not put back: "), (putBack, partlyPutBack, notPutBack));
        var done = Row("service:a", ChangeKind.Service, "Turned off Example Device Service (ExampleSvc)");
        var failed = Row("task:b", ChangeKind.ScheduledTask, "Turned off the scheduled task Vendor Updater");
        // Put back, with what did not happen all the same (a service not started again): a line of its own kind, never a
        // "Put back:" line, so no notice can show it as plainly put back.
        var partly = Row("service:c", ChangeKind.Service, "Turned off Downloaded Maps Manager (MapsBroker)");
        string summary = Program.UninstallSummary([done, failed, partly], null,
            [(done, new UndoResult(true, "It is back.")), (failed, new UndoResult(false, "Access is denied.")),
             (partly, new UndoResult(true, "Downloaded Maps Manager (MapsBroker): the start type is back to Automatic, but starting it again failed: error 1067.")
                 { Caveat = "the start type is back to Automatic, but starting it again failed: error 1067" })],
            new DateTime(2026, 10, 30, 9, 0, 0));
        var lines = summary.Split(Environment.NewLine);

        // As SummaryLines does: each line that starts with the prefix, without it.
        Assert.Equal([done.What], lines.Where(l => l.StartsWith(putBack, StringComparison.Ordinal)).Select(l => l[putBack.Length..]));
        Assert.Equal([partly.What + ". The start type is back to Automatic, but starting it again failed: error 1067."],
            lines.Where(l => l.StartsWith(partlyPutBack, StringComparison.Ordinal)).Select(l => l[partlyPutBack.Length..]));
        Assert.Equal([failed.What + ". Access is denied."], lines.Where(l => l.StartsWith(notPutBack, StringComparison.Ordinal)).Select(l => l[notPutBack.Length..]));
        // No prefix starts another, so each line is picked by one of them only.
        Assert.All(lines, l => Assert.True(new[] { putBack, partlyPutBack, notPutBack }.Count(p => l.StartsWith(p, StringComparison.Ordinal)) <= 1, l));
        // SummaryLines whole: -1 when the file cannot be read, else each line over every line of it, once, cut, and the count.
        Assert.Equal(["Result := ''", "Shown := ''", "Seen := #10", "Count := -1", "if not LoadStringsFromFile(DataDir + '\\uninstall-summary.txt', Lines) then Exit", "Count := 0",
            "for I := 0 to GetArrayLength(Lines) - 1 do if (Copy(Lines[I], 1, Length(Prefix)) = Prefix) and (Pos(#10 + Lines[I] + #10, Seen) = 0) then begin " +
            "Seen := Seen + Lines[I] + #10; AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), Max)); end",
            "Result := Shown + MoreLine(Count)"], Block("SummaryLines"));
        // Each line once: a cleanup run a second time in the same uninstall adds its own lines, and may say again what
        // could not be put back.
        Assert.Equal(["Seen := Seen + Lines[I] + #10", "AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), Max))"],
            Block("SummaryLines", ForEachLine + " if (Copy(Lines[I], 1, Length(Prefix)) = Prefix) and (Pos(#10 + Lines[I] + #10, Seen) = 0) then"));
        Assert.Empty(BlocksAround("PutBackDone", "Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count); " +
            "Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);"));
        Assert.Empty(BlocksAround("NotAllPutBack", "Lines := SummaryLines(DataDir, NotPutBackPrefix, MaxSummaryShown, Count);"));
        Assert.Empty(BlocksAround("NotAllPutBack", "Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);"));
        // The prefixes are written once, as the constants this test reads.
        Assert.Single(Regex.Matches(Code(), "'Put back: '"));
        Assert.Single(Regex.Matches(Code(), "'Partly put back: '"));
        Assert.Single(Regex.Matches(Code(), "'Could not put back: '"));
        // Nothing was changed: PoolWatch says so in a line of its own, which no prefix picks.
        string none = Program.UninstallSummary([], null, [], new DateTime(2026, 10, 30, 9, 0, 0));
        Assert.DoesNotContain(none.Split(Environment.NewLine), l => new[] { putBack, partlyPutBack, notPutBack }.Any(p => l.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void WhatItSaysAfterTheCleanupNamesWhatItPutBackAndWhereToLookWhenItCannotReadIt()
    {
        string done = Body("PutBackDone");
        Assert.Contains("if (Count < 0) or (PartCount < 0) then Result := 'PoolWatch put back every change it recorded. Its summary could not be read, " +
                        "so this does not say whether one ' + 'was put back only in part (a service not started again, say).' " +
                        "else if Count + PartCount = 0 then Result := 'PoolWatch had no changes on this PC to put back.' " +
                        "else if Count = 0 then Result := PartlyPutBackHeading + #13#10 + Part " +
                        "else if PartCount = 0 then Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines " +
                        "else Result := PartlyPutBackHeading + #13#10 + Part + #13#10#13#10 + 'PoolWatch put back each of these changes:' + #13#10 + Lines;", done);
        // The outcome's words, right after the two lists are read, in the routine's own block.
        Assert.Empty(BlocksAround("PutBackDone", "Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count); " +
            "Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount); " +
            "if (Count < 0) or (PartCount < 0) then Result := 'PoolWatch put back every change it recorded. Its summary could not be read, " +
            "so this does not say whether one ' + 'was put back only in part (a service not started again, say).' " +
            "else if Count + PartCount = 0 then Result := 'PoolWatch had no changes on this PC to put back.' " +
            "else if Count = 0 then Result := PartlyPutBackHeading + #13#10 + Part " +
            "else if PartCount = 0 then Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines " +
            "else Result := PartlyPutBackHeading + #13#10 + Part + #13#10#13#10 + 'PoolWatch put back each of these changes:' + #13#10 + Lines;"));
        // A summary that cannot be read is not taken for an empty one (-1, not 0).
        Assert.Empty(BlocksAround("SummaryLines", "Count := -1; if not LoadStringsFromFile(DataDir + '\\uninstall-summary.txt', Lines) then Exit; Count := 0;"));
        Assert.Empty(BlocksAround("NotAllPutBack", "Lines := SummaryLines(DataDir, NotPutBackPrefix, MaxSummaryShown, Count); " +
            "if Count > 0 then Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10 " +
            "else Result := 'Some changes could not be put back. '; " +
            "Result := Result + 'They stay listed in ' + DataDir + ', so PoolWatch can still put them back if you install it again. ' + " +
            "'uninstall-summary.txt there says what was put back and what was not, and why.';"));
        Assert.Contains("Count := -1; if not LoadStringsFromFile(", Body("SummaryLines"));
        Assert.Contains("Exit; Count := 0; for I := 0 to", Body("SummaryLines"));
        string notAll = Body("NotAllPutBack");
        Assert.Contains("if Count > 0 then Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10 " +
                        "else Result := 'Some changes could not be put back. ';", notAll);
        Assert.Contains("'uninstall-summary.txt there says what was put back and what was not, and why.'", notAll);
        Assert.Contains("if PartCount > 0 then Result := Result + #13#10#13#10 + PartlyPutBackHeading + #13#10 + Part;", notAll);
        Assert.Equal("PoolWatch put back these changes only in part:", Constant("PartlyPutBackHeading"));
    }

    /// <summary>
    /// The list a notice after the cleanup shows of the summary's lines that start with <paramref name="prefix"/>, as
    /// SummaryLines, AddShown and MoreLine in the script make it: each without the prefix, cut to <paramref name="max"/>
    /// characters (and to MaxLineShown), the first MaxShown named and the rest counted.
    /// </summary>
    private static (int Count, string Shown) SummaryLinesAsTheScript(string summary, string prefix, int max)
    {
        int maxShown = Number("MaxShown"), maxLine = Number("MaxLineShown");
        var shown = new StringBuilder();
        int count = 0;
        foreach (string line in summary.Split(Environment.NewLine))
            if (line.StartsWith(prefix, StringComparison.Ordinal) && ++count <= maxShown)
                shown.Append("\r\n- ").Append(Shorten(Shorten(line[prefix.Length..], max), maxLine));
        if (count > maxShown) shown.Append($"\r\nAnd {count - maxShown} more.");
        return (count, shown.ToString());
    }

    /// <summary>
    /// The notice once every change was put back, as PutBackDone in the script makes it from the summary (the list was
    /// read, with nothing that stays for a while after): what was put back only in part first, under its own heading,
    /// then what was put back.
    /// </summary>
    private static string PutBackDoneAsTheScript(string summary)
    {
        var (count, lines) = SummaryLinesAsTheScript(summary, Constant("PutBackPrefix"), Number("MaxSummaryShown"));
        var (partCount, part) = SummaryLinesAsTheScript(summary, Constant("PartlyPutBackPrefix"), Number("MaxLineShown"));
        string partly = Constant("PartlyPutBackHeading"), each = "PoolWatch put back each of these changes:";
        return count + partCount == 0 ? "PoolWatch had no changes on this PC to put back."
            : count == 0 ? partly + "\r\n" + part
            : partCount == 0 ? each + "\r\n" + lines
            : partly + "\r\n" + part + "\r\n\r\n" + each + "\r\n" + lines;
    }

    /// <summary>
    /// A change put back only in part (a service whose start type is back but that was not started again) is shown first
    /// in the notice after the cleanup, under its own heading, whole with what was not done: for a long display name, for
    /// a profile's name of the longest kind, and when ten or more other changes were put back too. It is never among the
    /// changes shown as put back, nor counted with them. This notice is the one beside the question whether to delete the
    /// data folder, and with it uninstall-summary.txt.
    /// </summary>
    [Fact]
    public void TheNoticeShowsEachChangePutBackOnlyInPartFirstWithWhatWasNotDone()
    {
        const string NotKnown = "the start type is back to Automatic, but it was not started again: PoolWatch does not know whether it was running when " +
                                "it was turned off. Start it from Services (services.msc) if you need it running";
        Assert.Equal(NotKnown, "the start type is back to Automatic, but " + PoolWatch.Core.Services.ServicePlan.RunningNotKnownText);
        const string Failed = "the start type is back to Automatic, but starting it again failed: error 1067";
        var plain = Enumerable.Range(1, 12).Select(i => Row($"startup:{i}", ChangeKind.StartupEntry, $"Turned off the startup app Example{i}")).ToList();
        // A profile's name of the longest kind (60 characters), and a third-party service's long display name.
        var diag = Row("service:diagtrack", ChangeKind.Service,
            $"Turned off Connected User Experiences and Telemetry (DiagTrack) (profile {new string('P', 60)}, at start)");
        var contoso = Row("service:contososvc", ChangeKind.Service,
            "Turned off Contoso Enterprise Endpoint Telemetry and Device Health Synchronisation Background Agent for Managed Workstations (x64) (ContosoSvc)");
        // Each put back only in part comes after the twelve others, as the 13th and 14th line.
        string summary = Program.UninstallSummary([.. plain, diag, contoso], null,
            [.. plain.Select(c => (c, new UndoResult(true, "It is back."))),
             (diag, new UndoResult(true, "...") { Caveat = Failed }), (contoso, new UndoResult(true, "...") { Caveat = NotKnown })],
            new DateTime(2026, 11, 1, 9, 0, 0));

        Assert.Contains("if Count > MaxShown then Result := #13#10 + Format('And %d more.', [Count - MaxShown]) else Result := '';", Body("MoreLine"));
        string notice = PutBackDoneAsTheScript(summary);
        // Each line of a list starts with a line break of its own, so a blank line follows each heading.
        Assert.Equal("PoolWatch put back these changes only in part:\r\n" +
                     $"\r\n- {diag.What}. The start type is back to Automatic, but starting it again failed: error 1067." +
                     $"\r\n- {contoso.What}. The start type is back to Automatic, but it was not started again: PoolWatch does not know whether it was " +
                     "running when it was turned off. Start it from Services (services.msc) if you need it running." +
                     "\r\n\r\nPoolWatch put back each of these changes:\r\n" +
                     string.Concat(plain.Take(10).Select(c => "\r\n- " + c.What)) + "\r\nAnd 2 more.", notice);

        // Eleven put back only in part, and nothing else: the first ten named with what was not done, the 11th counted
        // under the same heading; none is shown or counted as put back.
        var services = Enumerable.Range(1, 11).Select(i => Row($"service:s{i}", ChangeKind.Service, $"Turned off Example Service {i} (Svc{i})")).ToList();
        string only = PutBackDoneAsTheScript(Program.UninstallSummary(services, null,
            [.. services.Select(c => (c, new UndoResult(true, "...") { Caveat = Failed }))], new DateTime(2026, 11, 1, 9, 0, 0)));
        Assert.Equal("PoolWatch put back these changes only in part:\r\n" +
                     string.Concat(services.Take(10).Select(c => $"\r\n- {c.What}. The start type is back to Automatic, but starting it again failed: error 1067.")) +
                     "\r\nAnd 1 more.", only);
    }

    // ---------------------------------------------------------------- the choice

    [Fact]
    public void AnUninstallChoosesToPutTheChangesBackUnlessToldToKeepThemAndNeverAsksWhenSilentOrDeletingTheData()
    {
        // /KEEPCHANGES keeps; a silent uninstall (/SILENT, /VERYSILENT) and /DELETEDATA choose to put the changes back
        // without asking (ChoicePutBack: a change PoolWatch cannot put back stays); only an uninstall started by hand asks.
        Assert.Equal(["if HasSwitch('KEEPCHANGES') then Result := ChoiceKeep else if UninstallSilent or HasSwitch('DELETEDATA') then Result := ChoicePutBack else Result := ChoiceAsk"],
            Block("PutBackChoice"));
        Assert.Equal(["case PutBackChoice of ChoiceKeep: Result := True; ChoicePutBack: Result := False; else Result := not PutBackAnswer; end"], Block("KeepChangesWanted"));
        // Asked, putting the changes back is the first choice (the task dialog's default) and anything but Keep chooses it,
        // a box that could not be shown included.
        Assert.Empty(BlocksAround("PutBackAnswer", "SetArrayLength(Labels, 2); " +
            "Labels[0] := 'Put them back (recommended)' + #10 + 'Each change goes back to how it was before PoolWatch.'; " +
            "Labels[1] := 'Keep my changes' + #10 + 'They stay as they are. PoolWatch still removes its own sign-in task and desktop ' + " +
            "'shortcut, which would start files that are about to be removed.'; " +
            "try Result := TaskDialogMsgBox('Put back what PoolWatch changed?', Text, mbConfirmation, MB_YESNO, Labels, 0) <> IDNO; " +
            "except Log('The question could not be shown (' + GetExceptionMessage + '): every change is to be put back, the default.'); Result := True; end;"));
        // With nothing to put back, nothing is asked.
        Assert.Empty(BlocksAround("PutBackAnswer", "Result := True; if ListRead and (PutBackCount = 0) then Exit;"));
        // The list is read first, then the choice is made, logged as what is to be done, and passed to the cleanup, which
        // tries to put back every change (or, kept, only the logon task and the shortcut: Program.UndoSelection).
        Assert.Equal(["case CurUninstallStep of usAppMutexCheck:"], BlocksAround("CurUninstallStepChanged", "ReadChangeList(DataDir); KeepChanges := KeepChangesWanted; " +
            "if KeepChanges then Log('The changes are to be kept: only the logon task and the desktop shortcut are to go.') " +
            "else Log('Every change is to be put back (one PoolWatch cannot put back stays).'); " +
            "Code := RunCleanup(KeepChanges, False);"));
        Assert.Equal(["Params := '--uninstall-cleanup'", "if KeepChanges then Params := Params + ' --keep-changes'", "if Append then Params := Params + ' --append-summary'",
            "Launcher := LauncherPath(ExpandConstant('{app}'))",
            "if Launcher = '' then begin Log('The install folder has no launcher of the same version as PoolWatch''s program, ' + ProgramPath(ExpandConstant('{app}')) + " +
            "', so PoolWatch''s cleanup was not started.'); Result := NotInPlace; end " +
            "else if NameGoesBack(ExpandConstant('{app}'), PoolWatchName) or NameGoesBack(ExpandConstant('{app}'), SelfContainedName) then " +
            "begin Log('One of the launcher''s names in the install folder goes back only at the next restart, so PoolWatch''s cleanup ' + 'was not started.'); " +
            "Result := NotInPlace; end " +
            "else if Exec(Launcher, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then Log(Format('PoolWatch --uninstall-cleanup: exit code %d.', [Result])) " +
            "else begin Log(Format('PoolWatch could not be started for its cleanup (error %d).', [Result])); Result := -1; end"], Block("RunCleanup"));
        Assert.Null(Program.UndoSelection(keepChanges: false)); // every change is tried
        // The data folder is deleted only when every change was put back. Each is the step's own statement: the choice in
        // the cleanup's step, the delete in the post-uninstall step, which then says what of the folder is left.
        Assert.Equal(["case CurUninstallStep of usAppMutexCheck:"], BlocksAround("CurUninstallStepChanged", "ChangesPutBack := (Code = 0) and not KeepChanges;"));
        Assert.Equal(["case CurUninstallStep of usAppMutexCheck: begin ... end; usPostUninstall:"],
            BlocksAround("CurUninstallStepChanged", "if ChangesPutBack then if DeleteDataWanted(DataDir) then begin ... end;"));
        Assert.Equal(["DelTree(DataDir, True, True, True)", "SayDataLeft(DataDir)"], Block("CurUninstallStepChanged", DeletingTheData));
    }

    /// <summary>The heads of the post-uninstall step's block that deletes the data folder.</summary>
    private static readonly string[] DeletingTheData =
        ["case CurUninstallStep of usAppMutexCheck: begin ... end; usPostUninstall:", "if ChangesPutBack then if DeleteDataWanted(DataDir) then"];

    /// <summary>
    /// The uninstaller logs its choice as what is to be done, never as what the cleanup did. What it logs as the choice is
    /// made (here read from PutBackChoice, KeepChangesWanted and the question's PutBackAnswer, and from its cleanup step
    /// between the choice and the cleanup's first run) is logged before the cleanup runs, and a change PoolWatch cannot
    /// put back then stays (what was done is in uninstall-summary.txt, which the notices after the cleanup read). So each
    /// of those lines, read as it is logged (<see cref="LoggedTexts"/>), says what is to be done ("is to be put back",
    /// "are to go"), and none has a word that says what is done: a form of put, keep, go, remove or delete (put, puts,
    /// putting, keep, keeps, kept, keeping, go, goes, going, went, gone, remove, removes, removed, removing, delete,
    /// deletes, deleted or deleting), in any case, that does not follow "to", "to be" or "cannot". The rule is held to
    /// find each of those forms, in lower and in upper case, and none of them after those words. The installer's
    /// end-to-end test finds the two lines it reads by the whole line, each character that has a meaning in a regular
    /// expression escaped, and its check says only what the line shows: what the uninstall chose. Each of those checks
    /// is held as written, with its CHECK-RAN marker.
    /// </summary>
    [Fact]
    public void TheUninstallerLogsItsChoiceAsWhatIsToBeDoneNotAsWhatTheCleanupDid()
    {
        string step = Body("CurUninstallStepChanged");
        int chosen = step.IndexOf("KeepChanges := KeepChangesWanted;", StringComparison.Ordinal);
        int cleanup = chosen < 0 ? -1 : step.IndexOf("RunCleanup(", chosen, StringComparison.Ordinal);
        Assert.True(chosen >= 0 && cleanup > chosen, "the cleanup step makes the choice before the cleanup's first run");
        var logged = new[] { Body("PutBackChoice"), Body("KeepChangesWanted"), Body("PutBackAnswer"), step[chosen..cleanup] }.SelectMany(LoggedTexts).ToList();
        Assert.Equal(3, logged.Count); // the question that could not be shown, and each choice
        Regex toBeDone = ToBeDone, done = Done;
        Assert.All(logged, text => { Assert.Matches(toBeDone, text); Assert.DoesNotMatch(done, text); });
        // The rule finds the words these lines said before, and others that say what is done.
        Assert.All(new[] { "Every change is put back.", "The question could not be shown (<GetExceptionMessage>): every change is put back, the default.",
            "The changes are kept: only the logon task and the desktop shortcut go.", "PoolWatch puts back every change.", "Every change has been put back.",
            "The changes are to be kept: the logon task goes.",
            "The changes are to be kept: PoolWatch is removing only the logon task and the desktop shortcut.",
            "The changes are to be kept: only the logon task and the desktop shortcut are going." }, s => Assert.Matches(done, s));
        // It finds each form of the five words, in lower and in upper case, and none of them after "to", "to be" or "cannot".
        string[] forms = ["put", "puts", "putting", "keep", "keeps", "kept", "keeping", "go", "goes", "going", "went", "gone",
            "remove", "removes", "removed", "removing", "delete", "deletes", "deleted", "deleting"];
        Assert.All(forms, form =>
        {
            Assert.Matches(done, "The logon task: PoolWatch " + form + " it.");
            Assert.Matches(done, "The logon task: PoolWatch " + form.ToUpperInvariant() + " it.");
            Assert.All(new[] { "to", "TO", "to be", "To  Be", "cannot", "CANNOT" },
                before => Assert.DoesNotMatch(done, "The logon task: PoolWatch is " + before + " " + form + " it."));
        });
        Assert.Equal(["The question could not be shown (<GetExceptionMessage>): every change is to be put back, the default.",
            "The changes are to be kept: only the logon task and the desktop shortcut are to go.", "Every change is to be put back (one PoolWatch cannot put back stays)."], logged);
        // The end-to-end test's checks of those lines. PowerShell's -match is .NET's Regex, ignoring case.
        string e2e = Source("ci", "installer-e2e.ps1");
        static string Escaped(string text) => Regex.Replace(text, @"[\\.()\[\]{}*+?|^$#]", @"\$0");
        string putBack = Escaped(logged[2]), kept = Escaped(logged[1]);
        Assert.Equal(@"Every change is to be put back \(one PoolWatch cannot put back stays\)\.", putBack);
        Assert.All(new[] { logged[1], logged[2] }, text => Assert.Matches(new Regex(Escaped(text), RegexOptions.IgnoreCase), "2026-11-05 10:00:00.000   " + text));
        string[] checks =
        [
            $"Assert-True ($uninstallLog -match '{putBack}') 'a silent uninstall chose to put the changes back'",
            $"    Assert-True ($uninstallLog -match '{putBack}') 'a silent /DELETEDATA uninstall chose to put the changes back'",
            $"Assert-True ((Get-UninstallLog 'uninstall-2') -match '{kept}') 'the /KEEPCHANGES uninstall chose to keep the changes'",
        ];
        var lines = e2e.Replace("\r\n", "\n").Split('\n');
        Assert.All(checks, check => Assert.Single(lines, l => l == check));
        var unmarked = ChecksWithoutAMarker(e2e, checks);
        Assert.True(unmarked.Count == 0, "checks whose CHECK-RAN marker is not where it must be: " + string.Join(Environment.NewLine, unmarked));
    }

    // A line that says what is to be done, and a form of put, keep, go, remove or delete that says what is done.
    private static readonly Regex ToBeDone = new(@"\b(is|are)\s+to\s+(be\s+)?\w+", RegexOptions.IgnoreCase);
    private static readonly Regex Done = new(@"(?<!\b(to|to\s+be|cannot)\s+)\b(put|puts|putting|keep|keeps|kept|keeping|go|goes|" +
        @"going|went|gone|remove|removes|removed|removing|delete|deletes|deleted|deleting)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// The cleanup's second run, when part of PoolWatch may still run after the first (a file of it is in use, and a
    /// process with its name runs, or the processes could not be listed: WaitForApp's -1), is logged before it runs, so
    /// its line says that part of PoolWatch may still run and what that run is to do, by the same rule as the choice's
    /// lines (<see cref="ToBeDone"/>, and no word of <see cref="Done"/>), and says what it does when the changes are kept:
    /// it only removes the logon task and the desktop shortcut. The user guide, the design notes and the security notes
    /// say that run's work in the same terms (it undoes what that PoolWatch changed, only by removing those two when the
    /// changes are kept), and none says that it puts those two back. The installer's end-to-end test finds that line by
    /// its start, and its check says only what the line shows: that the uninstall chose to run its cleanup once more.
    /// </summary>
    [Fact]
    public void TheCleanupsSecondRunIsLoggedAsWhatItIsToDo()
    {
        string step = Body("CurUninstallStepChanged");
        int again = step.IndexOf("if Again then", StringComparison.Ordinal);
        int run = again < 0 ? -1 : step.IndexOf("RunCleanup(KeepChanges, True)", again, StringComparison.Ordinal);
        Assert.True(again >= 0 && run > again, "the second run is started after its line is logged");
        string line = Assert.Single(LoggedTexts(step[again..run]));
        Assert.Matches(ToBeDone, line);
        Assert.DoesNotMatch(Done, line);
        Assert.Equal("Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more: it is to stop a " +
            "PoolWatch started since and to undo what that one changed (when the changes are to be kept, only to remove the " +
            "logon task and the desktop shortcut).", line);
        // The run is chosen also when the processes could not be listed (-1), so the line says "may", and so do the user
        // guide, the design notes and the security notes.
        Assert.Contains("Again := (AppCount <> 0) and not RanAgain;", step);
        Assert.Contains("if Listed then Result := RunningFromApp(Service, Exe, Launcher, Second, True, True) else Result := -1;", Body("WaitForApp"));
        foreach (string doc in new[] { Source("docs", "USER-GUIDE.md"), Source("docs", "DESIGN.md"), Source("SECURITY.md") })
        {
            string said = OneLine(doc);
            Assert.Contains("PoolWatch still runs, or may (a file of it is still in use and its processes could not be listed)", said.Replace("still runs then, or may", "still runs, or may"));
            Assert.DoesNotMatch(@"if part of PoolWatch still runs( then)?, (the cleanup|it) runs", said);
            Assert.DoesNotContain("once more if part of PoolWatch still runs (", said);
            Assert.DoesNotMatch(@"put back what (it|that one) changed[^.;]{0,40}only (its|the) logon task", said);
        }
        // What that run does, in each of them: it undoes what that PoolWatch changed, only by removing the two when the
        // changes are kept.
        foreach (var (doc, said) in new[]
                 {
                     (Source("docs", "USER-GUIDE.md"), "to stop a PoolWatch started meanwhile and undo what it changed (when you keep the changes, only by removing its " +
                                                       "logon task and desktop shortcut)"),
                     (Source("docs", "DESIGN.md"), "and undo what that one changed (with `--keep-changes`, only by removing the logon task and the desktop " +
                                                   "shortcut, as in the first run)"),
                     (Source("SECURITY.md"), "(to stop a PoolWatch started meanwhile and undo what it changed: when you keep the changes, only by removing its " +
                                             "logon task and desktop shortcut)"),
                 })
            Assert.Contains(said, OneLine(doc));
        // The rule finds the words this line said before.
        Assert.Matches(Done, "Part of PoolWatch still runs after its cleanup, so the cleanup runs once more: a PoolWatch started " +
            "since is stopped, and what it changed is put back.");
        Assert.StartsWith("Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more", line, StringComparison.Ordinal);
        string check = "    Assert-True ($uninstallLog -match 'Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more') " +
                       "'the uninstall chose to run its cleanup once more'";
        string e2e = Source("ci", "installer-e2e.ps1");
        Assert.Single(e2e.Replace("\r\n", "\n").Split('\n'), l => l == check);
        Assert.Empty(ChecksWithoutAMarker(e2e, [check]));
        Assert.DoesNotContain("the cleanup runs once more", e2e);
    }

    /// <summary>
    /// Every line the uninstaller's step logs (CurUninstallStepChanged, read as <see cref="LoggedTexts"/> reads it) is
    /// either logged after the result it names, which it reads: what the cleanup said (NotRemovedText, after the first
    /// RunCleanup) and what RemoveRest gave Windows (RestartText and RestartLogNote, after RemoveRest); or it is logged
    /// before that result, and then says what is to be done, by the rule of the choice's lines (<see cref="ToBeDone"/>,
    /// and no word of <see cref="Done"/>): the two choices, the cleanup's second run, and what part of PoolWatch that may
    /// still run has in use when the files are to be removed. A line added to the step that is neither fails here. What
    /// the data folder's delete left is said after that delete, by SayDataLeft (<see
    /// cref="WhatIsLeftOfTheDataFolderIsSaidAfterItsDeleteNeverReadThroughALinkAndNeverLeftToTheRestart"/>).
    /// </summary>
    [Fact]
    public void EveryLineTheUninstallersStepLogsBeforeItsResultSaysWhatIsToBeDone()
    {
        string step = Body("CurUninstallStepChanged");
        var after = new Dictionary<string, string>
        {
            ["<NotRemovedText(Code)>"] = "Code := RunCleanup(KeepChanges, False);",
            ["<RestartText(App)>"] = "RemoveRest(App);",
            ["<RestartLogNote>"] = "RemoveRest(App);",
        };
        foreach (var (logged, result) in after)
        {
            int at = step.IndexOf("Log(" + logged[1..^1] + ")", StringComparison.Ordinal), from = step.IndexOf(result, StringComparison.Ordinal);
            Assert.True(from >= 0 && at > from, $"{logged} is logged after {result}");
        }
        var before = LoggedTexts(step).Where(t => !after.ContainsKey(t)).ToList();
        Assert.Equal(["The changes are to be kept: only the logon task and the desktop shortcut are to go.",
            "Every change is to be put back (one PoolWatch cannot put back stays).",
            "Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more: it is to stop a PoolWatch started since and to undo " +
            "what that one changed (when the changes are to be kept, only to remove the logon task and the desktop shortcut).",
            "Part of PoolWatch may still run (<RunningNames>): what it still has in use when the uninstaller comes to remove the files is to be given " +
            "to Windows to delete at the next restart."], before);
        Assert.All(before, text => { Assert.Matches(ToBeDone, text); Assert.DoesNotMatch(Done, text); });
        // Each is logged before the result it names: the choices and the second run before a RunCleanup, the last before
        // RemoveRest, in the step before it.
        int last = step.IndexOf("Log('Part of PoolWatch may still run (' + RunningNames", StringComparison.Ordinal);
        Assert.True(last >= 0 && last < step.IndexOf("usPostUninstall:", StringComparison.Ordinal) && step.IndexOf("RemoveRest(App);", StringComparison.Ordinal) > last);
        // The rule finds the words the last line said before.
        Assert.Matches(Done, "Part of PoolWatch may still run (<RunningNames>): a file it has in use is deleted at the next restart.");
        // The step logs nothing else: no Log call this reader does not read.
        Assert.Equal(LoggedTexts(step).Count(), Regex.Matches(step, @"\bLog\(").Count);
    }

    /// <summary>
    /// The text each Log call of <paramref name="code"/> (Pascal read as <see cref="Body"/> reads it) logs, in order: its
    /// string literals joined, a quote written twice read as one, and each other part of it (a call, a variable) shown as
    /// that part in angle brackets.
    /// </summary>
    private static IEnumerable<string> LoggedTexts(string code) =>
        Regex.Matches(code, @"\bLog\(((?:'(?:[^']|'')*'|\((?:'(?:[^']|'')*'|[^'()])*\)|[^'()])*)\)")
            .Select(call => string.Concat(Regex.Matches(call.Groups[1].Value, @"'((?:[^']|'')*)'|[^']+").Select(part =>
                part.Groups[1].Success ? part.Groups[1].Value.Replace("''", "'")
                : part.Value.Trim(' ', '+') is { Length: > 0 } other ? $"<{other}>" : "")));

    [Fact]
    public void ASilentUninstallShowsNoBoxAfterTheCleanup()
    {
        // Every box after the cleanup is behind "if not UninstallSilent"; the one before it (the cleanup failed: its Retry
        // box) aborts a silent uninstall instead of asking. Each is held as a whole statement with every block around it.
        string[] cleanup = ["case CurUninstallStep of usAppMutexCheck:"];
        string[] wait = [.. cleanup, "repeat ... until not Again", "while not CleanupDone(Code) do"];
        string[] guarded = [.. cleanup, "if not UninstallSilent then"];
        string[] post = ["case CurUninstallStep of usAppMutexCheck: begin ... end; usPostUninstall:", "if RestartGiven + RestartFailed + RestartStays > 0 then"];
        // The Retry box: all that the loop over a failed cleanup holds, so a silent uninstall aborts before the box.
        Assert.Equal(["Log(NotRemovedText(Code))", "if UninstallSilent then Abort", "if MsgBox(NotRemovedText(Code), mbError, MB_RETRYCANCEL) <> IDRETRY then Abort",
            "Code := RunCleanup(KeepChanges, Summarized)"], Block("CurUninstallStepChanged", wait));
        Assert.Equal(wait, BlocksAround("CurUninstallStepChanged", "if UninstallSilent then Abort; if MsgBox(NotRemovedText(Code), mbError, MB_RETRYCANCEL) <> IDRETRY then Abort;"));
        Assert.Equal(wait, BlocksAround("CurUninstallStepChanged", "if MsgBox(NotRemovedText(Code), mbError, MB_RETRYCANCEL) <> IDRETRY then Abort; Code := RunCleanup(KeepChanges, Summarized);"));
        // Past the loop the cleanup said 0, 8 or 12 (CleanupDone): a failed one never gets to the notices.
        Assert.Equal(["Result := (Code = 0) or (Code = ExitNotAllPutBack) or (Code = ExitOtherDataFolder)"], Block("CleanupDone"));
        // The guard and the step it is in: the notices are the step's own block's, after ChangesPutBack is set.
        Assert.Equal(cleanup, BlocksAround("CurUninstallStepChanged", "ChangesPutBack := (Code = 0) and not KeepChanges; if not UninstallSilent then begin ... end;"));
        // All that the guard's block holds: the notice for each outcome, each under its condition (8: what could not be put
        // back; 12, the folder PoolWatch used in place of the usual one, with "Keep my changes" first: the changes were
        // kept; 12 otherwise: what was put back there; the changes kept when asked: where their list stays). On such a
        // folder nothing there is read or deleted (ChangesPutBack stays false, so the data question is never asked).
        Assert.Equal(["if Code = ExitNotAllPutBack then MsgBox(NotAllPutBack(DataDir), mbInformation, MB_OK) " +
            "else if (Code = ExitOtherDataFolder) and KeepChanges then MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' + " +
            "'starts with PoolWatch.safe-). Your changes were kept: their list stays in that folder, so PoolWatch can still ' + " +
            "'put them back if you install it again. Neither folder is deleted: check them, then delete them yourself.', mbInformation, MB_OK) " +
            "else if Code = ExitOtherDataFolder then MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' + " +
            "'starts with PoolWatch.safe-). PoolWatch put back what it could from there; uninstall-summary.txt in that folder ' + " +
            "'says what was put back and what was not, and why. Neither folder is deleted: check them, then delete them ' + " +
            "'yourself.', mbInformation, MB_OK) " +
            "else if KeepChanges and (PutBackChoice = ChoiceAsk) then MsgBox('Your changes were kept. Their list stays in ' + DataDir + ', so PoolWatch can still put them back ' + " +
            "'if you install it again.', mbInformation, MB_OK)"], Block("CurUninstallStepChanged", guarded));
        // With "Keep my changes" the cleanup put nothing back (it returns 0, which PoolWatch makes 12 on such a folder).
        // 12, not 9: 9 is the code for private names that could not be opened, and 10 and 11 are the launcher's.
        Assert.Equal(12, Engine.ExitCodes.OtherDataFolder);
        Assert.Equal(12, Number("ExitOtherDataFolder"));
        Assert.Equal(Engine.ExitCodes.OtherDataFolder, Program.CleanupExit(0, otherDataFolder: true)); // what a kept-changes cleanup returns there
        // After the files are removed, what Windows deletes at the next restart is said in a box only when someone is
        // there; a silent uninstall logs it, and that its exit code is still 0 and how a script tells. All that the block holds.
        Assert.Equal(["Log(RestartText(App))", "Log(RestartLogNote)", "if not UninstallSilent then MsgBox(RestartText(App), mbInformation, MB_OK)"],
            Block("CurUninstallStepChanged", post));
        Assert.Equal(post[..1], BlocksAround("CurUninstallStepChanged", "RemoveRest(App); if RestartGiven + RestartFailed + RestartStays > 0 then begin ... end;"));
        // The routine's six boxes: the Retry box in the loop, the four notices in the guard's block and the restart notice
        // in its own, so none is elsewhere.
        var statements = ScriptCode.PascalStatements(CodeSection(), "CurUninstallStepChanged");
        int Boxes(string[] heads) => statements.Where(s => s.Heads.SequenceEqual(heads)).Sum(s => Regex.Matches(s.Statement, @"MsgBox\(").Count);
        Assert.Equal((6, 1, 4, 1), (Regex.Matches(Body("CurUninstallStepChanged"), @"MsgBox\(").Count, Boxes(wait), Boxes(guarded), Boxes(post)));
        // The data folder's question and the notices after /DELETEDATA: none when silent, each held whole with the blocks
        // around it. The boxes that name more of the changes put back only in part are shown from one place only, inside
        // the same guard as the notice.
        string[] deleting = ["if HasSwitch('DELETEDATA') then"], told = [.. deleting, "if not UninstallSilent then"];
        string[] asked = ["if HasSwitch('DELETEDATA') then begin ... end else if UninstallSilent then Result := False else"];
        Assert.Equal(2, Regex.Matches(Body("DeleteDataWanted"), @"MsgBox\(").Count);
        Assert.Empty(BlocksAround("DeleteDataWanted", "if HasSwitch('DELETEDATA') then begin ... end else if UninstallSilent then Result := False else begin ... end;"));
        Assert.Equal(deleting, BlocksAround("DeleteDataWanted", "if not UninstallSilent then begin ... end; Result := True;"));
        Assert.Equal(told, BlocksAround("DeleteDataWanted", "Text := PutBackDone(DataDir); if SummaryNamesMore(DataDir, PartCount) then if PartCount > MaxShown + 1 then " +
            "Text := Text + #13#10#13#10 + Format('The other %d changes put back only in part are named next, ' + 'with what was not done.', [PartCount - MaxShown]) " +
            "else if PartCount = MaxShown + 1 then Text := Text + #13#10#13#10 + 'The other change put back only in part is named next, with what was not done.'; " +
            "MsgBox(Text + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are to be deleted too.', mbInformation, MB_OK); " +
            "ShowRestPutBackInPart(DataDir);"));
        Assert.Equal(4, Block("DeleteDataWanted", told).Count);
        Assert.Equal(asked, BlocksAround("DeleteDataWanted", "Result := MsgBox(Text + #13#10#13#10 + 'Also delete PoolWatch''s reports, logs and settings (' + DataDir + ')?', " +
            "mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;"));
        Assert.Equal(asked, BlocksAround("DeleteDataWanted", "Text := PutBackDone(DataDir); if SummaryNamesMore(DataDir, PartCount) then begin ... end;"));
        Assert.Equal(3, Block("DeleteDataWanted", asked).Count);
        Assert.Equal(["procedure ShowRestPutBackInPart(", "ShowRestPutBackInPart("],
            Regex.Matches(Code(), @"(procedure )?ShowRestPutBackInPart\(").Select(m => m.Value));
    }

    /// <summary>
    /// The uninstaller's list, question and notices, whole: how it reads a switch of its command line (HasSwitch), cuts
    /// and counts what it shows (Shorten, AddShown, MoreLine), chooses whether to ask (PutBackChoice), reads PoolWatch's
    /// list of its changes (JsonText, ReadChangeList), asks (PutBackAnswer, KeepChangesWanted), reads
    /// uninstall-summary.txt (SummaryLines), and what it says after the cleanup and before it deletes the data folder
    /// (PutBackDone, NotAllPutBack, DeleteDataWanted). Each routine is held from its header to its last end;, its comments
    /// included, with every run of white space made one space, so any edit to one fails here unless it only changes how
    /// long a run of white space is, or which white space it is. The tests above say what these routines do and why.
    /// </summary>
    [Fact]
    public void TheUninstallersListQuestionAndNoticesAreHeldWhole()
    {
        Assert.Equal(OneLine("""
            function HasSwitch(const Name: String): Boolean;
            var
              I: Integer;
            begin
              Result := False;
              for I := 1 to ParamCount do
                if CompareText(ParamStr(I), '/' + Name) = 0 then
                  Result := True;
            end;
            """), Whole("HasSwitch"));
        Assert.Equal(OneLine("""
            function Shorten(const S: String; const Max: Integer): String;
            begin
              if Length(S) > Max then
                Result := Copy(S, 1, Max - 3) + '...'
              else
                Result := S;
            end;
            """), Whole("Shorten"));
        Assert.Equal(OneLine("""
            procedure AddShown(var List: String; var Count: Integer; const Line: String);
            begin
              Count := Count + 1;
              if Count <= MaxShown then
                List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown);
            end;
            """), Whole("AddShown"));
        Assert.Equal(OneLine("""
            function MoreLine(const Count: Integer): String;
            begin
              if Count > MaxShown then
                Result := #13#10 + Format('And %d more.', [Count - MaxShown])
              else
                Result := '';
            end;
            """), Whole("MoreLine"));
        Assert.Equal(OneLine("""
            function PutBackChoice: Integer;
            begin
              if HasSwitch('KEEPCHANGES') then
                Result := ChoiceKeep
              else if UninstallSilent or HasSwitch('DELETEDATA') then
                Result := ChoicePutBack
              else
                Result := ChoiceAsk;
            end;
            """), Whole("PutBackChoice"));
        Assert.Equal(OneLine("""
            function JsonText(const S: String; P: Integer): String;
            var
              C: Char;
            begin
              Result := '';
              while P <= Length(S) do
              begin
                C := S[P];
                if C = '"' then
                  Exit;
                if C = '\' then
                begin
                  P := P + 1;
                  if P > Length(S) then
                    Exit;
                  C := S[P];
                  if C = 'u' then
                  begin
                    Result := Result + ' ';
                    P := P + 4;
                  end
                  else if (C = 'b') or (C = 'f') or (C = 'n') or (C = 'r') or (C = 't') then
                    Result := Result + ' '
                  else
                    Result := Result + C;
                end
                else
                  Result := Result + C;
                P := P + 1;
              end;
            end;
            """), Whole("JsonText"));
        Assert.Equal(OneLine("""
            procedure ReadChangeList(const DataDir: String);
            var
              ListFile, Line, What, Leaves, Caveat, Shown, Launcher: String;
              Lines: TArrayOfString;
              Code, I: Integer;
            begin
              ListRead := False;
              PutBackList := '';
              LeftList := '';
              CaveatList := '';
              PutBackCount := 0;
              LeftCount := 0;
              ListFile := DataDir + '\' + ListFileName;
              { Through the launcher of the install folder's program, and only when there is one (PoolWatch is never started
                without it: LauncherPath). }
              Launcher := LauncherPath(ExpandConstant('{app}'));
              if (Launcher = '') or
                 not Exec(Launcher, '--changes-dump "' + ListFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
                Code := -1;
              { No list: the uninstaller touches nothing in the data folder, not even to tidy up. PoolWatch did not use that
                folder this time (it could not start, refused to use it, or keeps its data in a folder of its own), so a file
                there need not be PoolWatch's, and PoolWatch wrote none. }
              if Code <> 0 then
              begin
                Log(Format('PoolWatch could not list its changes (exit %d).', [Code]));
                Exit;
              end;
              { A list: PoolWatch has just checked that only administrators can change its data folder, and written the file there
                itself. }
              ListRead := LoadStringsFromFile(ListFile, Lines);
              DeleteFile(ListFile);
              if not ListRead then
              begin
                Log('PoolWatch''s list of changes could not be read.');
                Exit;
              end;
              What := '';
              Leaves := '';
              Caveat := '';
              for I := 0 to GetArrayLength(Lines) - 1 do
              begin
                Line := TrimRight(Lines[I]);
                if Line = '  {' then
                begin
                  What := '';
                  Leaves := '';
                  Caveat := '';
                end
                else if Copy(Line, 1, Length(WhatKey)) = WhatKey then
                  What := JsonText(Line, Length(WhatKey) + 1)
                else if Copy(Line, 1, Length(LeavesKey)) = LeavesKey then
                  Leaves := JsonText(Line, Length(LeavesKey) + 1)
                else if Copy(Line, 1, Length(CaveatKey)) = CaveatKey then
                  Caveat := JsonText(Line, Length(CaveatKey) + 1)
                else if ((Line = '  }') or (Line = '  },')) and (What <> '') then
                begin
                  if Leaves = '' then
                  begin
                    Log('Uninstalling puts back: ' + What);
                    AddShown(PutBackList, PutBackCount, Shorten(What, MaxNameShown));
                    if Caveat <> '' then
                    begin
                      Log('After it is put back: ' + Caveat);
                      Shown := Shorten(Caveat, MaxLineShown);
                      if Pos(Shown, CaveatList) = 0 then
                        CaveatList := CaveatList + #13#10 + Shown;
                    end;
                  end
                  else
                  begin
                    Log('Uninstalling cannot put back: ' + What + '. ' + Leaves);
                    AddShown(LeftList, LeftCount, Shorten(What, MaxNameShown) + '. ' + Leaves);
                  end;
                  What := '';
                  Leaves := '';
                  Caveat := '';
                end;
              end;
              if PutBackCount + LeftCount = 0 then
                Log('PoolWatch has no changes on this PC to put back.');
            end;
            """), Whole("ReadChangeList"));
        Assert.Equal(OneLine("""
            function PutBackAnswer: Boolean;
            var
              Text: String;
              Labels: TArrayOfString;
            begin
              Result := True;
              if ListRead and (PutBackCount = 0) then
                Exit;
              if ListRead then
              begin
                Text := 'These are the changes PoolWatch made on this PC that are still in effect. Uninstalling puts each one back ' +
                  'as it was before PoolWatch changed it:' + #13#10 + PutBackList + MoreLine(PutBackCount);
                if CaveatList <> '' then
                  Text := Text + #13#10 + CaveatList;
                if LeftCount > 0 then
                  Text := Text + #13#10#13#10 + 'Uninstalling cannot put these back:' + #13#10 + LeftList + MoreLine(LeftCount);
              end
              else
                Text := 'PoolWatch could not list its changes just now. Uninstalling puts back each change it recorded, or says ' +
                  'why it could not.' + #13#10#13#10 + CaveatIfGuardWasOn;
              SetArrayLength(Labels, 2);
              Labels[0] := 'Put them back (recommended)' + #10 + 'Each change goes back to how it was before PoolWatch.';
              Labels[1] := 'Keep my changes' + #10 + 'They stay as they are. PoolWatch still removes its own sign-in task and desktop ' +
                'shortcut, which would start files that are about to be removed.';
              try
                Result := TaskDialogMsgBox('Put back what PoolWatch changed?', Text, mbConfirmation, MB_YESNO, Labels, 0) <> IDNO;
              except
                Log('The question could not be shown (' + GetExceptionMessage + '): every change is to be put back, the default.');
                Result := True;
              end;
            end;
            """), Whole("PutBackAnswer"));
        Assert.Equal(OneLine("""
            function KeepChangesWanted: Boolean;
            begin
              case PutBackChoice of
                ChoiceKeep: Result := True;
                ChoicePutBack: Result := False;
              else
                Result := not PutBackAnswer;
              end;
            end;
            """), Whole("KeepChangesWanted"));
        Assert.Equal(OneLine("""
            function SummaryLines(const DataDir, Prefix: String; const Max: Integer; var Count: Integer): String;
            var
              Lines: TArrayOfString;
              Shown, Seen: String;
              I: Integer;
            begin
              Result := '';
              Shown := '';
              Seen := #10;
              Count := -1;
              if not LoadStringsFromFile(DataDir + '\uninstall-summary.txt', Lines) then
                Exit;
              Count := 0;
              for I := 0 to GetArrayLength(Lines) - 1 do
                if (Copy(Lines[I], 1, Length(Prefix)) = Prefix) and (Pos(#10 + Lines[I] + #10, Seen) = 0) then
                begin
                  Seen := Seen + Lines[I] + #10;
                  AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), Max));
                end;
              Result := Shown + MoreLine(Count);
            end;
            """), Whole("SummaryLines"));
        Assert.Equal(OneLine("""
            function PutBackDone(const DataDir: String): String;
            var
              Count, PartCount: Integer;
              Lines, Part: String;
            begin
              Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count);
              Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
              if (Count < 0) or (PartCount < 0) then
                Result := 'PoolWatch put back every change it recorded. Its summary could not be read, so this does not say whether one ' +
                  'was put back only in part (a service not started again, say).'
              else if Count + PartCount = 0 then
                Result := 'PoolWatch had no changes on this PC to put back.'
              else if Count = 0 then
                Result := PartlyPutBackHeading + #13#10 + Part
              else if PartCount = 0 then
                Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines
              else
                Result := PartlyPutBackHeading + #13#10 + Part + #13#10#13#10 + 'PoolWatch put back each of these changes:' + #13#10 + Lines;
              if (Count + PartCount <> 0) and (CaveatList <> '') then
                Result := Result + #13#10 + CaveatList
              else if (Count + PartCount <> 0) and not ListRead then
                Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
            end;
            """), Whole("PutBackDone"));
        Assert.Equal(OneLine("""
            function NotAllPutBack(const DataDir: String): String;
            var
              Count, PartCount: Integer;
              Lines, Part: String;
            begin
              Lines := SummaryLines(DataDir, NotPutBackPrefix, MaxSummaryShown, Count);
              if Count > 0 then
                Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10
              else
                Result := 'Some changes could not be put back. ';
              Result := Result + 'They stay listed in ' + DataDir + ', so PoolWatch can still put them back if you install it again. ' +
                'uninstall-summary.txt there says what was put back and what was not, and why.';
              Part := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
              if PartCount > 0 then
                Result := Result + #13#10#13#10 + PartlyPutBackHeading + #13#10 + Part;
              { What stays for a while after the ones that were put back, as before the cleanup. }
              if CaveatList <> '' then
                Result := Result + #13#10 + CaveatList
              else if not ListRead then
                Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;
            end;
            """), Whole("NotAllPutBack"));
        Assert.Equal(OneLine("""
            function DeleteDataWanted(const DataDir: String): Boolean;
            var
              Text: String;
              PartCount: Integer;
            begin
              if HasSwitch('DELETEDATA') then
              begin
                if not UninstallSilent then
                begin
                  Text := PutBackDone(DataDir);
                  if SummaryNamesMore(DataDir, PartCount) then
                    if PartCount > MaxShown + 1 then
                      Text := Text + #13#10#13#10 + Format('The other %d changes put back only in part are named next, ' +
                        'with what was not done.', [PartCount - MaxShown])
                    else if PartCount = MaxShown + 1 then
                      Text := Text + #13#10#13#10 + 'The other change put back only in part is named next, with what was not done.';
                  MsgBox(Text + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are to be deleted too.',
                    mbInformation, MB_OK);
                  ShowRestPutBackInPart(DataDir);
                end;
                Result := True;
              end
              else if UninstallSilent then
                Result := False
              else
              begin
                Text := PutBackDone(DataDir);
                if SummaryNamesMore(DataDir, PartCount) then
                begin
                  Text := Text + #13#10#13#10 + 'uninstall-summary.txt in ' + DataDir + ' names them all';
                  if PartCount > 0 then
                    Text := Text + ', with what was not done';
                  Text := Text + '. Answer No to keep it.';
                end;
                Result := MsgBox(Text + #13#10#13#10 + 'Also delete PoolWatch''s reports, logs and settings (' + DataDir + ')?',
                  mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
              end;
            end;
            """), Whole("DeleteDataWanted"));
    }

    /// <summary>
    /// Every routine of the script's [Code], each function and procedure, those Windows provides included, is held whole:
    /// this file's code has a statement <c>Assert.Equal(OneLine("""text"""), Whole("name"));</c> for each (as
    /// <see cref="WholePins"/> reads it: not in a comment, a string or the code of an <c>#if</c>), and this test holds each
    /// such text to its routine again, so a pin holds whichever method it is in. So any edit to a routine fails here unless
    /// it only changes how long a run of white space is, or which white space it is, and so does a routine added to [Code]
    /// without such a pin: the reader finds the routines in what Pascal Script reads as code, as it takes comments and
    /// strings as Pascal Script does (<see cref="ScriptCode.Pascal"/>), and one whose header does not start a line fails
    /// too (the names are read a second way, from the headers at the start of a line). What [Code] has outside its
    /// routines is held by <see cref="OutsideItsRoutinesTheCodeHasOnlyPlainConstantsAndVariables"/>; that Inno Setup's
    /// preprocessor passes [Code] on as it is (adding no routine either) by <see cref="ThePreprocessorPutsNothingIntoTheCode"/>;
    /// and that the code Inno Setup's compiler reads as [Code] is this section, from its one header line to the end of the
    /// script, by <see cref="CodeSectionOf"/> and <see cref="InstallerPagesTests.NoLineOfTheCodeBeginsWithABracketOrAHash"/>,
    /// which split the script's lines as Inno Setup reads the file and take the white space at their ends as its
    /// compiler does; the preprocessor joins none of them (<see cref="CodeSectionOf"/> fails on a line it would join to
    /// the next).
    /// </summary>
    [Fact]
    public void EveryRoutineOfTheScriptIsHeldWhole()
    {
        var routines = ScriptCode.PascalRoutineNames(CodeSection());
        // The same names as the headers at the start of a line of the code, read another way; a routine with a body and
        // one that Windows provides among them.
        Assert.Equal(Regex.Matches(ScriptCode.Pascal(CodeSection()), @"(?m)^(?:function|procedure)\s+(\w+)").Select(m => m.Groups[1].Value), routines);
        Assert.Contains("CurUninstallStepChanged", routines);
        Assert.Contains("OpenProcess", routines);
        var pins = WholePins(Source("tests", "PoolWatch.AppTests", "UninstallerTests.cs"));
        var unheld = routines.Where(r => !pins.Any(p => p.Name == r)).ToList();
        Assert.True(unheld.Count == 0, "routines of [Code] that no pin here holds whole: " + string.Join(", ", unheld));
        foreach (var (name, text) in pins)
            Assert.Equal(OneLine(text), Whole(name));
    }

    /// <summary>
    /// The pins of a C# source that hold a routine of [Code] whole, read in its code (<see cref="ScriptCode.CSharpTokens"/>):
    /// each statement <c>Assert.Equal(OneLine("""text"""), Whole("name"));</c>, its text a raw string and its name a plain
    /// one, with the routine's name and the raw string's text.
    /// </summary>
    internal static List<(string Name, string Text)> WholePins(string source)
    {
        var t = ScriptCode.CSharpTokens(source);
        string[] shape = ["Assert", ".", "Equal", "(", "OneLine", "(", "", ")", ",", "Whole", "(", "", ")", ")", ";"];
        var pins = new List<(string, string)>();
        for (int i = 0; i + shape.Length <= t.Count; i++)
        {
            if (!Enumerable.Range(0, shape.Length).All(j => shape[j].Length == 0 || t[i + j] == shape[j])) continue;
            string text = t[i + 6], name = t[i + 11];
            int quotes = text.TakeWhile(c => c == '"').Count();
            if (quotes < 3 || !Regex.IsMatch(name, @"^""\w+""$")) continue;
            pins.Add((name[1..^1], text[quotes..^quotes]));
        }
        return pins;
    }

    /// <summary>
    /// What [Code] has outside its routines (<see cref="ScriptCode.PascalOutsideRoutines"/>: every routine taken out from its
    /// header to its own last token, and every comment, each read as Pascal Script reads it: <see cref="ScriptCode.Pascal"/>)
    /// is its header and sections of constants and variables, each declared plainly (<see cref="PlainDeclarationsFailure"/>).
    /// Anything else there fails here: an event attribute (<c>&lt;event('InitializeSetup')&gt;</c> before a routine's
    /// header makes that routine run at that event too, and an InitializeSetup or InitializeUninstall that returns False
    /// ends the run), a type, a preprocessor directive, a statement, a section after [Code] (a line that starts one fails
    /// <see cref="InstallerPagesTests.NoLineOfTheCodeBeginsWithABracketOrAHash"/> too, in a comment as well). Nor does any
    /// text of [Code] start a compiler directive, which the reader would take for a comment (<c>{$</c> or <c>(*$</c>: in
    /// Pascal <c>{$I file}</c> reads in another file, and <c>{$IFDEF}</c> leaves code out). Pascal Script reads the
    /// same [Code] as the readers here (but for a line that is empty or holds only characters from #1 to #32, which the
    /// preprocessor may leave out), as the preprocessor joins none of its lines and changes none of them
    /// (<see cref="ThePreprocessorPutsNothingIntoTheCode"/>). So the code of [Code] is its routines, each held whole
    /// (<see cref="EveryRoutineOfTheScriptIsHeldWhole"/>), and those declarations, which are held only where a test
    /// reads them.
    /// </summary>
    [Fact]
    public void OutsideItsRoutinesTheCodeHasOnlyPlainConstantsAndVariables()
    {
        var outside = ScriptCode.PascalOutsideRoutines(CodeSection());
        Assert.Equal(["[", "Code", "]", "const"], outside.Take(4));
        Assert.Contains("var", outside);
        Assert.Null(PlainDeclarationsFailure(outside.Skip(3).ToList()));
        Assert.DoesNotMatch(@"\{\$|\(\*\$", CodeSection());
    }

    /// <summary>
    /// The words no plain declaration takes as a name: the 65 that Pascal Script's tokenizer reserves (its LookupTable in
    /// uPSUtils.pas, as Inno Setup 6.7.3 builds it), and Delphi's other reserved words.
    /// </summary>
    private static readonly HashSet<string> PascalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pascal Script's.
        "and", "array", "as", "begin", "case", "chr", "class", "const", "constructor", "destructor", "div", "do", "downto",
        "else", "end", "except", "exit", "export", "external", "finalization", "finally", "for", "forward", "function",
        "goto", "if", "implementation", "in", "inherited", "initialization", "interface", "is", "label", "mod", "nil", "not",
        "of", "or", "ord", "out", "override", "private", "procedure", "program", "property", "protected", "public",
        "published", "record", "repeat", "set", "shl", "shr", "then", "to", "try", "type", "unit", "until", "uses", "var",
        "virtual", "while", "with", "xor",
        // Delphi's others.
        "asm", "dispinterface", "exports", "file", "inline", "library", "object", "packed", "raise", "resourcestring",
        "string", "threadvar",
    };

    /// <summary>
    /// Why the Pascal tokens <paramref name="tokens"/> are not only sections of plain declarations; null when they are. A
    /// section is <c>const</c> or <c>var</c> (in any case) and at least one declaration: a constant is a name, <c>=</c>, one
    /// number (digits, or <c>$</c> and hex digits, with a minus sign before it or not) or one string, and <c>;</c>; a
    /// variable is one or more names separated by commas, <c>:</c>, a type (a name, or <c>array of</c> and a name, with
    /// bounds of two numbers, <c>[0..2]</c>, after <c>array</c> or not) and <c>;</c>. A name is a word of letters, digits
    /// and underscores that starts with a letter or an underscore and is not one of <see cref="PascalWords"/>, in any case:
    /// a word Pascal Script's tokenizer reserves (<c>begin</c>, <c>type</c>, <c>inherited</c> and the like) or another of
    /// Delphi's reserved words (<c>raise</c>, <c>threadvar</c> and the like); <c>String</c> is taken as a type's name.
    /// </summary>
    internal static string? PlainDeclarationsFailure(IReadOnlyList<string> tokens)
    {
        int i = 0;
        string At() => i < tokens.Count ? tokens[i] : "(the end)";
        bool Is(string word) => i < tokens.Count && tokens[i].Equals(word, StringComparison.OrdinalIgnoreCase);
        bool Name(bool type = false) =>
            i < tokens.Count && Regex.IsMatch(tokens[i], @"^[A-Za-z_]\w*$") && (!PascalWords.Contains(tokens[i]) || type && Is("String"));
        string Failure(string wanted) =>
            $"at token {i}, {wanted} was wanted, not \"{At()}\" (after: {string.Join(" ", tokens.Skip(Math.Max(0, i - 8)).Take(Math.Min(i, 8)))})";

        if (tokens.Count == 0) return "no section";
        while (i < tokens.Count)
        {
            bool constants = Is("const");
            if (!constants && !Is("var")) return Failure("const or var");
            i++;
            int declarations = 0;
            while (i < tokens.Count && !Is("const") && !Is("var"))
            {
                if (constants)
                {
                    if (!Name()) return Failure("a constant's name");
                    i++;
                    if (!Is("=")) return Failure("=");
                    i++;
                    if (Is("-")) i++;
                    bool literal = i < tokens.Count && (Regex.IsMatch(tokens[i], @"^(\d+|\$[0-9A-Fa-f]+)$") || tokens[i - 1] != "-" && Regex.IsMatch(tokens[i], @"^'(?:[^']|'')*'$"));
                    if (!literal) return Failure("one number or string");
                    i++;
                }
                else
                {
                    while (true)
                    {
                        if (!Name()) return Failure("a variable's name");
                        i++;
                        if (!Is(",")) break;
                        i++;
                    }
                    if (!Is(":")) return Failure(":");
                    i++;
                    if (Is("array"))
                    {
                        i++;
                        if (Is("["))
                        {
                            foreach (string bound in new[] { "a number", ".", ".", "a number", "]" })
                            {
                                i++;
                                if (bound == "a number" ? i >= tokens.Count || !Regex.IsMatch(tokens[i], @"^\d+$") : !Is(bound)) return Failure(bound);
                            }
                            i++;
                        }
                        if (!Is("of")) return Failure("of");
                        i++;
                    }
                    if (!Name(type: true)) return Failure("a type's name");
                    i++;
                }
                if (!Is(";")) return Failure(";");
                i++;
                declarations++;
            }
            if (declarations == 0) return Failure("a declaration");
        }
        return null;
    }

    /// <summary>
    /// The lines of a setup script's [Code] (<see cref="CodeSectionOf"/>), from its header line on, each with its number
    /// in the script.
    /// </summary>
    internal static List<(int Number, string Text)> CodeLinesOf(string script)
    {
        var code = IssLines(CodeSectionOf(script));
        int before = IssLines(script).Count - code.Count;
        return [.. code.Select((l, i) => (before + i + 1, l.Text))];
    }

    /// <summary>
    /// The lines of a setup script's [Code] that Inno Setup's preprocessor (ISPP) does not pass on as written, each with
    /// its number and what it is: a line with an inline directive (<c>{#</c>, which ISPP expands in a comment or a string
    /// too), a directive line (<see cref="IsDirectiveLine"/>: one whose first character other than #1 to #32 is
    /// <c>#</c>, such as an <c>#include</c>, a <c>#define</c> or an <c>#if</c>), and a line ISPP takes out as a comment
    /// of its own (<see cref="IsPreprocessorComment"/>: one whose first such characters are <c>//</c>; inside a brace
    /// comment, such a line could hold the brace that ends it, and without it Pascal Script reads on as a comment). A
    /// line that ISPP joins to the next, in [Code] or before it, fails <see cref="CodeSectionOf"/>, which this reads the
    /// lines of [Code] with.
    /// </summary>
    internal static List<string> CodeLinesThePreprocessorChanges(string script)
    {
        var changed = new List<string>();
        foreach (var (number, text) in CodeLinesOf(script))
        {
            if (text.Contains("{#", StringComparison.Ordinal)) changed.Add($"line {number}, an inline directive: {text}");
            if (IsDirectiveLine(text)) changed.Add($"line {number}, a directive line: {text}");
            if (IsPreprocessorComment(text)) changed.Add($"line {number}, a line the preprocessor takes out as a comment: {text}");
        }
        return changed;
    }

    /// <summary>
    /// Inno Setup's preprocessor (ISPP), which runs over the script's lines before Inno Setup compiles them, passes [Code]
    /// on as it is. It joins none of the script's lines to the next, as no line of the script longer than two characters
    /// ends in white space (a character up to #32) and a backslash (<see cref="CodeSectionOf"/> holds that, with
    /// <see cref="JoinsTheNextLine"/>: the ISPPBuiltins.iss that ISPP reads in before the script makes the backslash its
    /// span symbol, and ISPP joins no line of two characters or fewer), and no line of [Code] is one it changes
    /// (<see cref="CodeLinesThePreprocessorChanges"/>: an inline directive, a directive line, or a line it takes out as a
    /// comment of its own). The script's directive lines are these nine, all before [Code], so [Code] is outside every
    /// <c>#if</c>; none of them is a <c>#pragma</c> (which can change what ISPP takes for an inline directive, or which
    /// lines it joins: <c>#pragma spansymbol</c>) or an <c>#include</c> (whose file no test reads); and each inline
    /// directive of the script, all in the sections before [Code], gives AppVersion, NumericVersion or PublishDir. So, as
    /// far as the script decides, the [Code] the tests read is the [Code] Inno Setup compiles (ISPP may also leave out a
    /// line that is empty or holds only characters from #1 to #32, which neither adds nor hides code), and a routine the
    /// preprocessor would add fails here instead of escaping <see cref="EveryRoutineOfTheScriptIsHeldWhole"/>. What
    /// ISCC's command line gives is not read here (installer/build.ps1 passes AppVersion, PublishDir and the output
    /// folder, and Sign and its sign tool when it signs).
    /// </summary>
    [Fact]
    public void ThePreprocessorPutsNothingIntoTheCode()
    {
        string script = Script();
        Assert.Empty(CodeLinesThePreprocessorChanges(script));
        Assert.Equal(
        [
            "#ifndef AppVersion",
            "  #error Pass /DAppVersion=<version> (the Version in Directory.Build.props)",
            "#endif",
            "#ifndef PublishDir",
            "  #error Pass /DPublishDir=<the staged self-contained build>",
            "#endif",
            "#define NumericVersion Pos(\"-\", AppVersion) > 0 ? Copy(AppVersion, 1, Pos(\"-\", AppVersion) - 1) : AppVersion",
            "#ifdef Sign",
            "#endif",
        ], IssLines(script).Select(l => l.Text).Where(IsDirectiveLine));
        int inline = Regex.Matches(script, @"\{#").Count;
        Assert.True(inline > 0, "the script has no inline directive");
        Assert.Equal(inline, Regex.Matches(script, @"\{#(AppVersion|NumericVersion|PublishDir)\}").Count);
    }


    // ---------------------------------------------------------------- PoolWatch still running, and files in use

    /// <summary>
    /// When PoolWatch's files are replaced and removed. The setup replaces them in place only when Windows has no work in
    /// the install folder for its next restart and none of the files of PoolWatch's program there is in use (asked of the
    /// files themselves); it then renames every old file aside before it writes any new one, deletes the old ones once
    /// every new one is in place, and puts every one back if it does not get that far. Otherwise it finishes at that
    /// restart: [Files] puts the new files in a folder of their own and Windows moves them all into place then (Inno Setup's
    /// RestartReplace, MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT, which leaves Inno Setup's restart state alone), also when
    /// it is run again before that restart, which it decides before Inno Setup's own check for an unfinished earlier
    /// install looks at Windows' list. Its uninstaller goes in a folder of its own when one of the uninstaller's files in
    /// the install folder is in use. The setup and the uninstaller name what has each file in use (Restart Manager). The
    /// uninstaller removes no file after a cleanup that failed; once the cleanup is done, what is still in use is deleted
    /// at the next restart. A token-leak check has a helper (PoolWatch.exe --token-leak-test) that holds no mutex and can
    /// run on after PoolWatch exits, which the setup and the uninstaller end themselves. PoolWatch's one-instance mutex and
    /// its exit signal are in a private namespace this script does not open, so it looks at no named object. This test
    /// holds the whole text of each routine of these steps, the declarations of the functions of Windows they call
    /// included, from its header to its last end; (a declaration to its end), with every run of white space made one
    /// space, so any edit to one fails it except one that only changes how long a run of white space is, or which white
    /// space it is. The tests after it say why each rule is there.
    /// </summary>
    [Fact]
    public void EachFunctionThatDecidesWhenFilesGoIsHeldWhole()
    {
        Assert.Equal(OneLine("""
            function OpenProcess(DesiredAccess: DWORD; InheritHandle: BOOL; ProcessId: DWORD): THandle;
              external 'OpenProcess@kernel32.dll stdcall';
            """), Whole("OpenProcess"));
        Assert.Equal(OneLine("""
            function QueryFullProcessImageNameW(Process: THandle; Flags: DWORD; ExeName: String; var Size: DWORD): BOOL;
              external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
            """), Whole("QueryFullProcessImageNameW"));
        Assert.Equal(OneLine("""
            function TerminateProcess(Process: THandle; Code: DWORD): BOOL;
              external 'TerminateProcess@kernel32.dll stdcall';
            """), Whole("TerminateProcess"));
        Assert.Equal(OneLine("""
            function CloseHandle(Opened: THandle): BOOL;
              external 'CloseHandle@kernel32.dll stdcall';
            """), Whole("CloseHandle"));
        Assert.Equal(OneLine("""
            function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
              external 'OpenEventW@kernel32.dll stdcall';
            """), Whole("OpenEvent"));
        Assert.Equal(OneLine("""
            function SetEvent(Event: THandle): BOOL;
              external 'SetEvent@kernel32.dll stdcall';
            """), Whole("SetEvent"));
        Assert.Equal(OneLine("""
            function GetFileVersionInfoSizeW(Filename: String; var Handle: DWORD): DWORD;
              external 'GetFileVersionInfoSizeW@version.dll stdcall delayload';
            """), Whole("GetFileVersionInfoSizeW"));
        Assert.Equal(OneLine("""
            function GetFileVersionInfoW(Filename: String; Handle, Size: DWORD; Data: String): BOOL;
              external 'GetFileVersionInfoW@version.dll stdcall delayload';
            """), Whole("GetFileVersionInfoW"));
        Assert.Equal(OneLine("""
            function RmStartSession(var Session: DWORD; Flags: DWORD; Key: String): DWORD;
              external 'RmStartSession@rstrtmgr.dll stdcall delayload';
            """), Whole("RmStartSession"));
        Assert.Equal(OneLine("""
            function RmRegisterResources(Session: DWORD; FileCount: DWORD; Files: TArrayOfString; ProcessCount: DWORD;
              Processes: TArrayOfString; ServiceCount: DWORD; Services: TArrayOfString): DWORD;
              external 'RmRegisterResources@rstrtmgr.dll stdcall delayload';
            """), Whole("RmRegisterResources"));
        Assert.Equal(OneLine("""
            function RmGetList(Session: DWORD; var Needed, Count: DWORD; Infos: String; var Reasons: DWORD): DWORD;
              external 'RmGetList@rstrtmgr.dll stdcall delayload';
            """), Whole("RmGetList"));
        Assert.Equal(OneLine("""
            function RmEndSession(Session: DWORD): DWORD;
              external 'RmEndSession@rstrtmgr.dll stdcall delayload';
            """), Whole("RmEndSession"));
        Assert.Equal(OneLine("""
            function CharAt(const S: String; I: Integer): Char;
            begin
              if (I >= 1) and (I <= Length(S)) then
                Result := S[I]
              else
                Result := #0;
            end;
            """), Whole("CharAt"));
        Assert.Equal(OneLine("""
            function IsHelperLine(const Line: String): Boolean;
            var
              I: Integer;
            begin
              Result := False;
              I := 1;
              while (CharAt(Line, I) = ' ') or (CharAt(Line, I) = #9) do
                I := I + 1;
              if CharAt(Line, I) = '"' then
              begin
                I := I + 1;
                while (CharAt(Line, I) <> '"') and (CharAt(Line, I) <> #0) do
                  I := I + 1;
                if CharAt(Line, I) = #0 then
                  Exit;
                I := I + 1;
              end
              else
                while (CharAt(Line, I) <> ' ') and (CharAt(Line, I) <> #9) and (CharAt(Line, I) <> #0) do
                  I := I + 1;
              if (CharAt(Line, I) <> ' ') and (CharAt(Line, I) <> #9) then
                Exit;
              while (CharAt(Line, I) = ' ') or (CharAt(Line, I) = #9) do
                I := I + 1;
              Result := (Copy(Line, I, Length(HelperOption)) = HelperOption) and
                ((CharAt(Line, I + Length(HelperOption)) = #0) or (CharAt(Line, I + Length(HelperOption)) = ' ') or
                 (CharAt(Line, I + Length(HelperOption)) = #9));
            end;
            """), Whole("IsHelperLine"));
        Assert.Equal(OneLine("""
            function SecondLauncherPath(const App: String): String;
            begin
              Result := App + '\' + SelfContainedName;
            end;
            """), Whole("SecondLauncherPath"));
        Assert.Equal(OneLine("""
            function LauncherFile(const App: String): String;
            begin
              Result := App + '\' + PoolWatchName;
              if not FileExists(Result) and FileExists(SecondLauncherPath(App)) then
                Result := SecondLauncherPath(App);
            end;
            """), Whole("LauncherFile"));
        Assert.Equal(OneLine("""
            function SelfContainedNameThere: Boolean;
            begin
              if not SecondNameLooked then
              begin
                SecondNameLooked := True;
                SecondNameWas := FileExists(SecondLauncherPath(ExpandConstant('{app}')));
              end;
              Result := SecondNameWas;
            end;
            """), Whole("SelfContainedNameThere"));
        Assert.Equal(OneLine("""
            function ProgramPath(const App: String): String;
            begin
              Result := App + '\' + AppFolder + '\' + PoolWatchName;
            end;
            """), Whole("ProgramPath"));
        Assert.Equal(OneLine("""
            function HasLauncher(const App: String): Boolean;
            begin
              Result := FileExists(ProgramPath(App));
            end;
            """), Whole("HasLauncher"));
        Assert.Equal(OneLine("""
            function ProductVersionOf(const Path: String): String;
            var
              Handle, Size: DWORD;
              Data: String;
              P, V: Integer;
            begin
              Result := '';
              try
                Handle := 0;
                Size := GetFileVersionInfoSizeW(Path, Handle);
                if Size = 0 then
                  Exit;
                SetLength(Data, (Size + 1) div 2);
                if not GetFileVersionInfoW(Path, 0, Size, Data) then
                  Exit;
                P := Pos('ProductVersion' + #0, Data);
                if (P < 4) or (Ord(CharAt(Data, P - 2)) = 0) then
                  Exit;
                V := P + Length('ProductVersion') + 1;
                if (V mod 2) = 0 then
                  V := V + 1;
                while CharAt(Data, V) <> #0 do
                begin
                  Result := Result + CharAt(Data, V);
                  V := V + 1;
                end;
              except
                Log('The version of ' + Path + ' could not be read: ' + GetExceptionMessage);
                Result := '';
              end;
            end;
            """), Whole("ProductVersionOf"));
        Assert.Equal(OneLine("""
            function IsLauncherOf(const Path, App: String): Boolean;
            var
              Version: String;
            begin
              Result := False;
              if not FileExists(Path) or not HasLauncher(App) then
                Exit;
              Version := ProductVersionOf(Path);
              Result := (Version <> '') and (Version = ProductVersionOf(ProgramPath(App)));
            end;
            """), Whole("IsLauncherOf"));
        Assert.Equal(OneLine("""
            function LauncherPath(const App: String): String;
            begin
              Result := '';
              if IsLauncherOf(App + '\' + PoolWatchName, App) then
                Result := App + '\' + PoolWatchName
              else if IsLauncherOf(SecondLauncherPath(App), App) then
                Result := SecondLauncherPath(App);
            end;
            """), Whole("LauncherPath"));
        Assert.Equal(OneLine("""
            function PoolWatchProcesses(Service: Variant; const Exe, ShortExe, ShortLauncher, ShortSecond: String; Only: Integer): Variant;
            var
              Query: String;
            begin
              Query := 'SELECT Handle, ProcessId, CommandLine FROM Win32_Process WHERE (Name = "' +
                ExtractFileName(Exe) + '" OR Name = "' + ExtractFileName(ShortExe) + '"';
              if ShortLauncher <> '' then
                Query := Query + ' OR Name = "' + ExtractFileName(ShortLauncher) + '"';
              if ShortSecond <> '' then
                Query := Query + ' OR Name = "' + SelfContainedName + '" OR Name = "' + ExtractFileName(ShortSecond) + '"';
              Query := Query + ')';
              if Only <> 0 then
                Query := Query + ' AND ProcessId = ' + IntToStr(Only);
              Result := Service.ExecQuery(Query);
            end;
            """), Whole("PoolWatchProcesses"));
        Assert.Equal(OneLine("""
            function ImagePathOf(Id: Integer): String;
            var
              Handle: THandle;
              Buffer: String;
              Size: DWORD;
            begin
              Result := '';
              Handle := OpenProcess(ReadRights, False, Id);
              if Handle = 0 then
                Exit;
              try
                SetLength(Buffer, 32768);
                Size := Length(Buffer);
                if QueryFullProcessImageNameW(Handle, 0, Buffer, Size) then
                  Result := Copy(Buffer, 1, Size);
              finally
                CloseHandle(Handle);
              end;
            end;
            """), Whole("ImagePathOf"));
        Assert.Equal(OneLine("""
            function FromApp(Item: Variant; const ShortExe, ShortLauncher, ShortSecond: String): Integer;
            var
              Image, ShortImage: String;
            begin
              Image := ImagePathOf(Item.ProcessId);
              if Image = '' then
                Result := -1
              else
              begin
                ShortImage := GetShortName(Image);
                if CompareText(ShortImage, ShortExe) = 0 then
                  Result := 1
                else if ((ShortLauncher <> '') and (CompareText(ShortImage, ShortLauncher) = 0)) or
                        ((ShortSecond <> '') and (CompareText(ShortImage, ShortSecond) = 0)) then
                  Result := 2
                else
                  Result := 0;
              end;
            end;
            """), Whole("FromApp"));
        Assert.Equal(OneLine("""
            function CommandLineOf(Item: Variant): String;
            var
              Value: Variant;
            begin
              Value := Item.CommandLine;
              if VarIsNull(Value) then
                Result := ''
              else
                Result := Value;
            end;
            """), Whole("CommandLineOf"));
        Assert.Equal(OneLine("""
            function OwnerOf(Item: Variant): String;
            var
              Owner: Variant;
              User, Domain: String;
            begin
              Result := 'an account Windows did not name';
              try
                Owner := Item.ExecMethod_('GetOwner');
                if (Owner.ReturnValue = 0) and not VarIsNull(Owner.User) then
                begin
                  User := Owner.User;
                  if VarIsNull(Owner.Domain) then
                    Result := User
                  else
                  begin
                    Domain := Owner.Domain;
                    Result := Domain + '\' + User;
                  end;
                end;
              except
                Log('The account that runs a PoolWatch process could not be read: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("OwnerOf"));
        Assert.Equal(OneLine("""
            procedure EndHelper(Service: Variant; const Exe, ShortExe, ShortLauncher, ShortSecond: String; Id: Integer);
            var
              Opened: THandle;
              Found, Item: Variant;
            begin
              Opened := OpenProcess(EndRights, False, Id);
              if Opened = 0 then
              begin
                Log(Format('The token-leak test helper (process %d) could not be opened to end it (error %d).', [Id, DLLGetLastError]));
                Exit;
              end;
              try
                Found := PoolWatchProcesses(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id);
                if Found.Count <> 1 then
                  Log(Format('The token-leak test helper (process %d) has ended by itself.', [Id]))
                else
                begin
                  Item := Found.ItemIndex(0);
                  if (FromApp(Item, ShortExe, ShortLauncher, ShortSecond) <> 1) or not IsHelperLine(CommandLineOf(Item)) then
                    Log(Format('Process %d is no longer a token-leak test helper that runs from the install folder: it is left as it is.', [Id]))
                  else if TerminateProcess(Opened, 1) then
                    Log(Format('Ended the token-leak test helper (process %d) that still ran from the install folder.', [Id]))
                  else
                    Log(Format('The token-leak test helper (process %d) could not be ended (error %d).', [Id, DLLGetLastError]));
                end;
              except
                Log(Format('Whether process %d is still a token-leak test helper could not be checked, so it is left as it is: %s', [Id, GetExceptionMessage]));
              end;
              CloseHandle(Opened);
            end;
            """), Whole("EndHelper"));
        Assert.Equal(OneLine("""
            function RunningFromApp(Service: Variant; const Exe, Launcher, Second: String; Logged, EndHelpers: Boolean): Integer;
            var
              ShortExe, ShortLauncher, ShortSecond, Line, Shown, Who, Which: String;
              Found, Item: Variant;
              I, Id, Total, Where, Others, Unknown, Named: Integer;
            begin
              ShortExe := GetShortName(Exe);
              ShortLauncher := '';
              if Launcher <> '' then
                ShortLauncher := GetShortName(Launcher);
              ShortSecond := '';
              if Second <> '' then
                ShortSecond := GetShortName(Second);
              Result := 0;
              Others := 0;
              Unknown := 0;
              Named := 0;
              RunningNames := '';
              try
                Found := PoolWatchProcesses(Service, Exe, ShortExe, ShortLauncher, ShortSecond, 0);
                Total := Found.Count;
                for I := 0 to Total - 1 do
                begin
                  Item := Found.ItemIndex(I);
                  Id := Item.ProcessId;
                  Line := CommandLineOf(Item);
                  if Line = '' then
                    Shown := '(command line not readable)'
                  else
                    Shown := Line;
                  Where := FromApp(Item, ShortExe, ShortLauncher, ShortSecond);
                  if Where = 2 then
                    Which := 'its launcher, '
                  else
                    Which := '';
                  if Where <> 0 then
                  begin
                    Who := OwnerOf(Item);
                    if Named < MaxProcessesShown then
                    begin
                      if RunningNames <> '' then
                        RunningNames := RunningNames + '; ';
                      if Where > 0 then
                        RunningNames := RunningNames + Format('%sprocess %d, run by %s', [Which, Id, Who])
                      else
                        RunningNames := RunningNames + Format('process %d, run by %s, whose exe Windows did not give', [Id, Who]);
                      Named := Named + 1;
                    end;
                  end;
                  if Where > 0 then
                  begin
                    Result := Result + 1;
                    if (Where = 2) or not IsHelperLine(Line) then
                      Others := Others + 1;
                    if Logged then
                      Log(Format('PoolWatch still runs from the install folder: %sprocess %d, run by %s: %s', [Which, Id, Who, Shown]));
                  end
                  else if Where < 0 then
                  begin
                    Unknown := Unknown + 1;
                    if Logged then
                      Log(Format('Process %d has PoolWatch''s name but its exe could not be read, so it may run from the install folder (run by %s): %s', [Id, Who, Shown]));
                  end;
                end;
                if Result + Unknown > MaxProcessesShown then
                  RunningNames := RunningNames + Format('; and %d more', [Result + Unknown - MaxProcessesShown]);
                if Unknown > 0 then
                  Result := -1
                else if EndHelpers and (Result > 0) and (Others = 0) then
                  for I := 0 to Total - 1 do
                  begin
                    Item := Found.ItemIndex(I);
                    if FromApp(Item, ShortExe, ShortLauncher, ShortSecond) = 1 then
                    begin
                      Id := Item.ProcessId;
                      EndHelper(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id);
                    end;
                  end;
              except
                Log('The processes that run from the install folder could not be listed: ' + GetExceptionMessage);
                Result := -1;
              end;
            end;
            """), Whole("RunningFromApp"));
        Assert.Equal(OneLine("""
            function CanReplace(const Path: String): Boolean;
            var
              Probe: TFileStream;
            begin
              try
                Probe := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
                Probe.Free;
                Result := True;
              except
                Result := False;
              end;
            end;
            """), Whole("CanReplace"));
        Assert.Equal(OneLine("""
            function ConnectWmi: Variant;
            var
              Locator: Variant;
            begin
              Locator := CreateOleObject('WbemScripting.SWbemLocator');
              Result := Locator.ConnectServer('.', 'root\CIMV2');
            end;
            """), Whole("ConnectWmi"));
        Assert.Equal(OneLine("""
            function AccountOf(Id: Integer): String;
            var
              Found: Variant;
              Key: String;
              P: Integer;
            begin
              Key := '|' + IntToStr(Id) + '=';
              P := Pos(Key, Accounts);
              if P > 0 then
              begin
                Result := Copy(Accounts, P + Length(Key), Length(Accounts));
                Result := Copy(Result, 1, Pos('|', Result) - 1);
                Exit;
              end;
              Result := 'an account Windows did not name';
              try
                if AccountWmi = 0 then
                begin
                  AccountWmi := -1;
                  AccountService := ConnectWmi;
                  AccountWmi := 1;
                end;
                if AccountWmi = 1 then
                begin
                  Found := AccountService.ExecQuery('SELECT Handle, ProcessId FROM Win32_Process WHERE ProcessId = ' + IntToStr(Id));
                  if Found.Count = 1 then
                    Result := OwnerOf(Found.ItemIndex(0));
                end;
              except
                Log(Format('The account that runs process %d could not be read: %s', [Id, GetExceptionMessage]));
              end;
              Accounts := Accounts + Key + Result + '|';
            end;
            """), Whole("AccountOf"));
        Assert.Equal(OneLine("""
            function HoldersOf(const Path: String): String;
            var
              Session, Needed, Count, Reasons, Code: DWORD;
              Key, Infos, Name: String;
              Files, NoList: TArrayOfString;
              I, N, Base, C, Id, K: Integer;
            begin
              Result := '';
              try
                Session := 0;
                SetLength(Key, RmKeyChars);
                if RmStartSession(Session, 0, Key) <> 0 then
                begin
                  Result := ' (Windows could not say what has it open)';
                  Exit;
                end;
                try
                  SetArrayLength(Files, 1);
                  Files[0] := Path;
                  Count := 0;
                  Code := RmRegisterResources(Session, 1, Files, 0, NoList, 0, NoList);
                  if Code = 0 then
                  begin
                    Count := RmMaxRead;
                    SetLength(Infos, Count * RmInfoChars);
                    Needed := 0;
                    Reasons := 0;
                    Code := RmGetList(Session, Needed, Count, Infos, Reasons);
                    if Code = ErrorMoreData then
                    begin
                      Count := Needed;
                      SetLength(Infos, Count * RmInfoChars);
                      Code := RmGetList(Session, Needed, Count, Infos, Reasons);
                    end;
                  end;
                  if Code <> 0 then
                    Result := ' (Windows could not say what has it open)'
                  else if Count = 0 then
                    Result := ' (Windows named no program that has it open)'
                  else
                  begin
                    N := Count;
                    for I := 0 to N - 1 do
                    begin
                      Base := I * RmInfoChars + 1;
                      Id := Ord(CharAt(Infos, Base)) + Ord(CharAt(Infos, Base + 1)) * 65536;
                      Name := '';
                      C := Base + 6;
                      while (C < Base + 6 + 256) and (CharAt(Infos, C) <> #0) do
                      begin
                        K := Ord(CharAt(Infos, C));
                        if (K < 32) or ((K >= 127) and (K < 160)) or (K = 8232) or (K = 8233) then
                          Name := Name + ' '
                        else
                          Name := Name + CharAt(Infos, C);
                        C := C + 1;
                      end;
                      if Result <> '' then
                        Result := Result + '; ';
                      Result := Result + Format('%s, process %d, run by %s', [Name, Id, AccountOf(Id)]);
                    end;
                    Result := ' (in use by ' + Result + ')';
                  end;
                finally
                  RmEndSession(Session);
                end;
              except
                Log('What has ' + Path + ' open could not be read: ' + GetExceptionMessage);
                Result := ' (Windows could not say what has it open)';
              end;
            end;
            """), Whole("HoldersOf"));
        Assert.Equal(OneLine("""
            procedure FindHeld(const Dir, Rel: String; Logged: Boolean);
            var
              Find: TFindRec;
              Name, Path, Holders: String;
              Skip, Own: Boolean;
            begin
              if not FindFirst(Dir + '\*', Find) then
                Exit;
              try
                repeat
                  Name := Find.Name;
                  Path := Dir + '\' + Name;
                  Own := (Rel = '') and (CompareText(Copy(Name, 1, 5), 'unins') = 0);
                  Skip := (Name = '.') or (Name = '..') or
                    ((Rel = '') and ((CompareText(Name, StageFolder) = 0) or (CompareText(Name, UninstallFolder) = 0)));
                  if not Skip then
                  begin
                    if (Find.Attributes and AttrFolder) <> 0 then
                    begin
                      if (Find.Attributes and AttrLink) = 0 then
                        FindHeld(Path, Rel + Name + '\', Logged);
                    end
                    else if Own then
                    begin
                      if not IsUninstaller and not CanReplace(Path) then
                      begin
                        HeldUninstaller := True;
                        if Logged then
                          Log('In use, so Setup writes its uninstaller in a folder of its own: ' + Path + HoldersOf(Path));
                      end;
                    end
                    else if not CanReplace(Path) then
                    begin
                      Holders := '';
                      if Logged then
                        Holders := HoldersOf(Path);
                      AddShown(HeldList, HeldCount, Rel + Name + Holders);
                      HeldAll := HeldAll + #13#10 + Rel + Name + Holders;
                      if Logged then
                        Log('In use, so it cannot be replaced or removed now: ' + Path + Holders);
                    end;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("FindHeld"));
        Assert.Equal(OneLine("""
            function HeldFiles(const App: String; Logged: Boolean): Integer;
            begin
              HeldList := '';
              HeldAll := '';
              HeldCount := 0;
              HeldUninstaller := False;
              if Logged then
                Accounts := '';
              FindHeld(App, '', Logged);
              Result := HeldCount;
            end;
            """), Whole("HeldFiles"));
        Assert.Equal(OneLine("""
            function WorkFor(const ValueName, Folder: String): Boolean;
            var
              Work: String;
            begin
              Result := False;
              if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then
              begin
                Work := Lowercase(Work) + #0;
                Result := (Pos(Folder + '\', Work) > 0) or (Pos(Folder + #0, Work) > 0);
              end;
            end;
            """), Whole("WorkFor"));
        Assert.Equal(OneLine("""
            function RestartWorkPending(const App: String): Boolean;
            begin
              Result := WorkFor('PendingFileRenameOperations', Lowercase('\??\' + App)) or
                WorkFor('PendingFileRenameOperations2', Lowercase('\??\' + App));
            end;
            """), Whole("RestartWorkPending"));
        Assert.Equal(OneLine("""
            function GoesAtRestart(const Path: String): Boolean;
            var
              Froms, Tos: TArrayOfString;
              I: Integer;
            begin
              Result := False;
              ReadRestartWork(Froms, Tos);
              for I := 0 to GetArrayLength(Froms) - 1 do
                if Froms[I] = Lowercase(Path) then
                  Result := True;
            end;
            """), Whole("GoesAtRestart"));
        Assert.Equal(OneLine("""
            function RunStamp: String;
            begin
              if Stamp = '' then
                Stamp := GetDateTimeString('yyyymmddhhnnsszzz', #0, #0);
              Result := Stamp;
            end;
            """), Whole("RunStamp"));
        Assert.Equal(OneLine("""
            procedure FinishAtNextRestart(const Why: String);
            begin
              FinishAtRestart := True;
              StageDir := ExpandConstant('{app}') + '\' + StageFolder + '\' + RunStamp;
              if FinishReason <> '' then
                FinishReason := FinishReason + #13#10#13#10;
              FinishReason := FinishReason + Why;
              Log('Setup puts PoolWatch''s files in place at the next restart of Windows, from ' + StageDir + '. ' + Why);
            end;
            """), Whole("FinishAtNextRestart"));
        Assert.Equal(OneLine("""
            procedure LookForRestartWork(const App: String);
            begin
              if RestartWorkLooked then
                Exit;
              RestartWorkLooked := True;
              if RestartWorkPending(App) then
                FinishAtNextRestart('An earlier setup or uninstall of PoolWatch finishes when Windows restarts, so this one does too, after it.');
            end;
            """), Whole("LookForRestartWork"));
        Assert.Equal(OneLine("""
            function ProgramDir(Param: String): String;
            begin
              LookForRestartWork(ExpandConstant('{app}'));
              if FinishAtRestart then
                Result := StageDir
              else
                Result := ExpandConstant('{app}');
            end;
            """), Whole("ProgramDir"));
        Assert.Equal(OneLine("""
            function UninstallDir(Param: String): String;
            begin
              if UninstallerDir = '' then
              begin
                UninstallerDir := ExpandConstant('{app}');
                if FinishAtRestart or HeldUninstaller then
                  UninstallerDir := UninstallerDir + '\' + UninstallFolder + '\' + RunStamp;
                Log('Setup writes its uninstaller in ' + UninstallerDir + '.');
              end;
              Result := UninstallerDir;
            end;
            """), Whole("UninstallDir"));
        Assert.Equal(OneLine("""
            function StartsNow: Boolean;
            begin
              Result := not FinishAtRestart;
            end;
            """), Whole("StartsNow"));
        Assert.Equal(OneLine("""
            function PutBack(const Target, Aside: String): Boolean;
            var
              How: String;
            begin
              Result := True;
              if FileExists(Target) or not FileExists(Aside) then
                Exit;
              if RenameFile(Aside, Target) then
              begin
                Log('Put back ' + Target + '.');
                Exit;
              end;
              if FileCopy(Aside, Target, True) then
                How := ' as a copy: '
              else if CreateHardLinkW(Target, Aside, 0) then
                How := ' under a second name of the same file: '
              else
                How := '';
              if How <> '' then
              begin
                Log('Put back ' + Target + How + Aside + ' is in use' + HoldersOf(Aside) + '.');
                try
                  RestartReplace(Aside, '');
                except
                  Log('Windows could not be given ' + Aside + ' to delete at its next restart: ' + GetExceptionMessage);
                end;
              end
              else
              begin
                Result := False;
                try
                  RestartReplace(Aside, Target);
                  Log('Could not put back ' + Target + ' now, as ' + Aside + ' is in use' + HoldersOf(Aside) + ': Windows moves it back at its next restart.');
                except
                  Refused := Refused + 1;
                  Log('Could not put back ' + Target + ' from ' + Aside + ', now or at the next restart, so it stays set aside: ' +
                    GetExceptionMessage);
                end;
              end;
            end;
            """), Whole("PutBack"));
        Assert.Equal(OneLine("""
            procedure PutAllBack(Later: Boolean);
            var
              I, Pass, Back: Integer;
              Late, KeepAll: Boolean;
            begin
              Late := Later and (GetArrayLength(AsideTargets) > 0);
              KeepAll := LauncherLeftNew(ExpandConstant('{app}'));
              Back := 0;
              for Pass := 1 to 2 do
                for I := 0 to GetArrayLength(AsideTargets) - 1 do
                  if StartsPoolWatch(AsideTargets[I]) = (Pass = 2) then
                  begin
                    if Late and (LeftNew <> '') and ((Pass = 2) or KeepAll) then
                      Log('A new file stays after the next restart, so ' + AsideTargets[I] + ' stays set aside as ' + AsideNames[I] +
                        ' then too, so that nothing starts PoolWatch from old and new files mixed.')
                    else if Late then
                      MoveBackAtRestart(AsideTargets[I], AsideNames[I])
                    else if not PutBack(AsideTargets[I], AsideNames[I]) then
                      Late := True
                    else if Pass = 2 then
                      Back := Back + 1;
                  end;
              if Late then
              begin
                OldAtRestart := True;
                if KeepAll then
                  Log('A new file that starts PoolWatch could not be taken back, nor given to Windows to delete at its next ' +
                    'restart: every old file stays set aside, so that it starts nothing from them.')
                else if LeftNew <> '' then
                  Log('Not every new file could be taken back, nor given to Windows to delete at its next restart: the other old ' +
                    'files go back then, where Windows took them, and the old launcher stays set aside.')
                else if Back = 0 then
                  Log('Not every old file of PoolWatch''s could be put back now: Windows puts back the rest at its next restart, ' +
                    'the launcher last, so the old launcher is not back to start PoolWatch until then.')
                else
                  Log('One of the old launcher''s names could not be put back now: Windows puts it back at its next restart. Every ' +
                    'other old file is back.');
              end;
              SetArrayLength(AsideTargets, 0);
              SetArrayLength(AsideNames, 0);
            end;
            """), Whole("PutAllBack"));
        Assert.Equal(OneLine("""
            procedure SetAsideIn(const Dir, Rel: String; var Blocked: String);
            var
              Find: TFindRec;
              Found: TArrayOfString;
              Name, Path, Aside: String;
              I, N: Integer;
            begin
              if Blocked <> '' then
                Exit;
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  Name := Find.Name;
                  if (Name <> '.') and (Name <> '..') and
                     ((Rel <> '') or ((CompareText(Name, StageFolder) <> 0) and (CompareText(Name, UninstallFolder) <> 0) and
                      (CompareText(Copy(Name, 1, 5), 'unins') <> 0))) and
                     (((Find.Attributes and AttrFolder) = 0) or ((Find.Attributes and AttrLink) = 0)) then
                  begin
                    N := GetArrayLength(Found);
                    SetArrayLength(Found, N + 1);
                    if (Find.Attributes and AttrFolder) <> 0 then
                      Found[N] := '\' + Name
                    else
                      Found[N] := Name;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              I := 0;
              while (Blocked = '') and (I < GetArrayLength(Found)) do
              begin
                if Copy(Found[I], 1, 1) = '\' then
                begin
                  Name := Copy(Found[I], 2, Length(Found[I]) - 1);
                  SetAsideIn(Dir + '\' + Name, Rel + Name + '\', Blocked);
                end
                else
                begin
                  Path := Dir + '\' + Found[I];
                  Aside := Path + '.' + RunStamp + '.old';
                  if SetAsideSoon(Path, Aside) then
                  begin
                    N := GetArrayLength(AsideTargets);
                    SetArrayLength(AsideTargets, N + 1);
                    SetArrayLength(AsideNames, N + 1);
                    AsideTargets[N] := Path;
                    AsideNames[N] := Aside;
                  end
                  else
                    Blocked := Path;
                end;
                I := I + 1;
              end;
            end;
            """), Whole("SetAsideIn"));
        Assert.Equal(OneLine("""
            procedure SetAllAside(const App: String);
            var
              Blocked, Why: String;
              I: Integer;
            begin
              if FinishAtRestart then
                Exit;
              SelfContainedNameThere;
              Blocked := '';
              Why := '';
              try
                SetAsideIn(App, '', Blocked);
                I := 0;
                while (Blocked = '') and (I < GetArrayLength(AsideNames)) do
                begin
                  if not FreeSoon(AsideNames[I]) then
                    Blocked := AsideNames[I];
                  I := I + 1;
                end;
                if Blocked <> '' then
                  Why := Blocked + HoldersOf(Blocked) + ' came into use while Setup was setting the old files aside';
              except
                Why := 'The old files could not all be set aside (' + GetExceptionMessage + ')';
              end;
              if Why = '' then
                Log(Format('Set aside the %d old files of PoolWatch''s program, which this setup replaces now.', [GetArrayLength(AsideNames)]))
              else
              begin
                PutAllBack(False);
                Why := Why + ', so none of them is replaced now.';
                if HeldFiles(App, True) > 0 then
                begin
                  FinishFiles := HeldAll;
                  Why := Why + ' Each file in use now is listed below with what has it open.';
                end;
                FinishAtNextRestart(Why);
              end;
            end;
            """), Whole("SetAllAside"));
        Assert.Equal(OneLine("""
            procedure SaveLinkBefore;
            var
              Link: String;
            begin
              Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
              LinkBefore := '';
              if FileExists(Link) then
                if FileCopy(Link, ExpandConstant('{tmp}\PoolWatch.lnk'), False) then
                  LinkBefore := ExpandConstant('{tmp}\PoolWatch.lnk')
                else
                  Log('The Start menu entry could not be copied, so a setup that does not finish cannot put it back: ' + Link);
            end;
            """), Whole("SaveLinkBefore"));
        Assert.Equal(OneLine("""
            procedure DeleteNowOrAtRestart(const Path: String);
            begin
              if DeleteFile(Path) then
                Log('Deleted ' + Path + '.')
              else
                try
                  RestartReplace(Path, '');
                  Log('In use, so Windows deletes it at its next restart: ' + Path + HoldersOf(Path));
                except
                  Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
                end;
            end;
            """), Whole("DeleteNowOrAtRestart"));
        Assert.Equal(OneLine("""
            procedure DropAside;
            var
              I: Integer;
            begin
              for I := 0 to GetArrayLength(AsideTargets) - 1 do
                if FileExists(AsideTargets[I]) or (CompareText(ExtractFileExt(AsideTargets[I]), '.old') = 0) or
                   (CompareText(ExtractFileName(AsideTargets[I]), KeepName) = 0) then
                  DeleteNowOrAtRestart(AsideNames[I])
                else if Ships(AsideTargets[I]) then
                  Log('Setup did not write ' + AsideTargets[I] + ', which this version has, so the old one stays set aside as ' +
                    AsideNames[I] + ' and is not put back.')
                else
                  PutBack(AsideTargets[I], AsideNames[I]);
              SetArrayLength(AsideTargets, 0);
              SetArrayLength(AsideNames, 0);
            end;
            """), Whole("DropAside"));
        Assert.Equal(OneLine("""
            procedure DeinitializeSetup;
            var
              I: Integer;
              Later, Held: Boolean;
              App, Link, Text: String;
            begin
              if InstallDone or not InstallBegun then
                Exit;
              App := ExpandConstant('{app}');
              Later := False;
              if not FinishAtRestart then
                for I := 0 to GetArrayLength(Shipped) - 1 do
                  if not TakeBack(Shipped[I]) then
                    Later := True;
              PutAllBack(Later);
              if not FinishAtRestart then
                DropNewFolders(App, Later);
              PutUninstallerBack;
              PutEntryBack;
              Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
              if (LinkBefore <> '') and not FileExists(Link) then
                if FileCopy(LinkBefore, Link, True) then
                  Log('Put back the Start menu entry, ' + Link + '.')
                else
                  Log('Could not put back the Start menu entry, ' + Link + '.');
              if OldAtRestart then
              begin
                Held := True;
                if OldUninstaller then
                  Held := HoldOldUninstaller(App);
                Text := 'Setup did not finish, and another program has some of PoolWatch''s files open, so Setup could not put ' +
                  'every file of the version you had back now';
                if LauncherLeftNew(App) then
                  Text := Text + ', and a new file that starts PoolWatch stays after the next restart too, as Windows would not ' +
                    'take it to delete then: so that nothing starts PoolWatch from old and new files mixed, every file of the ' +
                    'version you had stays under the name Setup set it aside under (Setup''s log names each), and PoolWatch may ' +
                    'not start after that restart until the setup has run again'
                else
                begin
                  Text := Text + ': Windows puts back the rest when it next restarts';
                  if Refused > 0 then
                    Text := Text + ', but for what it would not take for that restart: any old file it would not take to put ' +
                      'back stays under the name Setup set it aside under, and any new file it would not take to delete stays ' +
                      'after the restart (Setup''s log names each), so PoolWatch may not start after that restart until the ' +
                      'setup has run again';
                  if LeftNew <> '' then
                    Text := Text + '. As a new file stays after the restart under its own name, the files that start the version ' +
                      'you had stay set aside then too, so that they start nothing from old and new files mixed';
                end;
                Text := Text + '. Until that restart ' + UntilRestart(App) + '.';
                if not Held then
                  Text := Text + ' Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not ' +
                    'be kept from running until then (Setup''s log says why), and it would not wait for the restart. It would ' +
                    'remove PoolWatch''s Apps and Features entry and keep its changes and its sign-in task, and the files Windows ' +
                    'puts back at the restart would then have no entry.'
                else if UninstallerProgramHeld then
                  Text := Text + ' Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if ' +
                    'Windows offers to remove PoolWatch from its list of apps before then, answer No.'
                else if OldUninstaller or (Refused = 0) then
                  Text := Text + ' Until that restart PoolWatch''s uninstaller removes nothing.';
                if Refused > 0 then
                  Text := Text + ' Run the setup again before you uninstall PoolWatch.'
                else
                  Text := Text + ' Uninstall PoolWatch, if you want to, after the restart.';
                Text := Text + ' Setup does not restart Windows: restart it when it suits you.';
                if HeldFiles(App, True) > 0 then
                  Text := Text + ' The files in use now, each with what has it open (Setup''s log, ' + ExpandConstant('{log}') +
                    ', names every one):' + HeldList + MoreLine(HeldCount)
                else
                  Text := Text + ' Setup''s log, ' + ExpandConstant('{log}') + ', names each file that was in use, and what had it open.';
                Log(Text);
                if not WizardSilent then
                  MsgBox(Text, mbInformation, MB_OK);
              end;
            end;
            """), Whole("DeinitializeSetup"));
        Assert.Equal(OneLine("""
            procedure KeepDrawn(Step, Steps: Integer);
            begin
              if WaitPage <> nil then
                WaitPage.SetProgress(Step, Steps);
            end;
            """), Whole("KeepDrawn"));
        Assert.Equal(OneLine("""
            function WaitForApp(const App: String; Seconds: Integer): Integer;
            var
              Waited, Quarter: Integer;
              Service: Variant;
              Listed, Held: Boolean;
              Exe, Launcher, Second: String;
            begin
              RunningNames := '';
              Second := '';
              if HasLauncher(App) then
              begin
                Exe := ProgramPath(App);
                Launcher := LauncherFile(App);
                if FileExists(SecondLauncherPath(App)) and (CompareText(Launcher, SecondLauncherPath(App)) <> 0) then
                  Second := SecondLauncherPath(App);
              end
              else
              begin
                Exe := LauncherFile(App);
                Launcher := '';
              end;
              Listed := True;
              try
                Service := ConnectWmi;
              except
                Log('The processes that run from the install folder could not be listed: ' + GetExceptionMessage);
                Listed := False;
              end;
              if Listed then
                Result := RunningFromApp(Service, Exe, Launcher, Second, True, True)
              else
                Result := -1;
              Waited := 0;
              Held := HeldFiles(App, False) > 0;
              while Held and (Waited < Seconds) and ((Result <> 0) or (Waited < BriefHold)) do
              begin
                for Quarter := 1 to 4 do
                begin
                  KeepDrawn(Waited * 4 + Quarter, Seconds * 4);
                  Sleep(250);
                  if StoppingOld then
                    SetOldExit(False);
                end;
                Waited := Waited + 1;
                if Listed then
                  Result := RunningFromApp(Service, Exe, Launcher, Second, False, True);
                Held := HeldFiles(App, False) > 0;
              end;
              if not Held and (Result <> 0) then
              begin
                Log('No file of PoolWatch''s program in the install folder is in use, so no process runs one; what the last look at ' +
                  'the list of processes found is not counted.');
                Result := 0;
              end;
              if Waited > 0 then
              begin
                if Result > 0 then
                  Log(Format('%d PoolWatch processes still run from the install folder after %d s: %s.', [Result, Waited, RunningNames]))
                else if Result = 0 then
                  Log(Format('After %d s no process of PoolWatch''s runs from the install folder.', [Waited]))
                else
                  Log(Format('After %d s whether PoolWatch still runs from the install folder could still not be checked.', [Waited]));
              end;
            end;
            """), Whole("WaitForApp"));
        Assert.Equal(OneLine("""
            procedure ShowWaitPage;
            begin
              if (WaitPage = nil) or WizardSilent then
                Exit;
              try
                WaitPage.SetText('Waiting for PoolWatch to close...', '');
                WaitPage.SetProgress(0, RunningWait);
                WaitPage.Show;
              except
                Log('The page for the wait could not be shown: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("ShowWaitPage"));
        Assert.Equal(OneLine("""
            procedure HideWaitPage;
            begin
              if (WaitPage = nil) or WizardSilent then
                Exit;
              try
                WaitPage.Hide;
              except
                Log('The page for the wait could not be hidden: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("HideWaitPage"));
        Assert.Equal(OneLine("""
            procedure WhenToReplace(const App: String);
            var
              Held, Uninstaller: Boolean;
              Quarter: Integer;
            begin
              LookForRestartWork(App);
              Held := HeldFiles(App, True) > 0;
              Uninstaller := HeldUninstaller;
              Quarter := 0;
              while Held and (Quarter < BriefHold * 4) do
              begin
                Sleep(250);
                Quarter := Quarter + 1;
                Held := HeldFiles(App, Quarter = BriefHold * 4) > 0;
                Uninstaller := Uninstaller or HeldUninstaller;
              end;
              HeldUninstaller := Uninstaller;
              if Held then
              begin
                FinishFiles := HeldAll;
                FinishAtNextRestart('Some of PoolWatch''s files are in use, so they cannot be replaced now. Each is listed below with what has it open.');
              end
              else if not FinishAtRestart then
              begin
                Log('No file in the install folder is in use: Setup replaces them now.');
                if DirExists(App + '\' + StageFolder) then
                  if not DelTree(App + '\' + StageFolder, True, True, True) then
                    Log('Files an earlier setup left for a restart that has happened since could not all be deleted: ' + App + '\' + StageFolder);
              end;
              if HeldUninstaller then
                Log('One of the uninstaller''s files in the install folder is in use, so Setup writes its uninstaller in a folder of its own.');
            end;
            """), Whole("WhenToReplace"));
        Assert.Equal(OneLine("""
            function PrepareToInstall(var NeedsRestart: Boolean): String;
            var
              Code: Integer;
              App, Launcher: String;
            begin
              Result := '';
              App := ExpandConstant('{app}');
              if CompareText(App, ExpandConstant('{autopf}\PoolWatch')) <> 0 then
              begin
                Result := 'PoolWatch installs only to ' + ExpandConstant('{autopf}\PoolWatch') + '. Run the setup without /DIR.';
                Exit;
              end;
              if FileExists(LauncherFile(App)) or HasLauncher(App) then
              begin
                ShowWaitPage;
                try
                  Launcher := LauncherPath(App);
                  if Launcher = '' then
                    StopBeforeLauncher
                  else if Exec(Launcher, '--stop-running', '', SW_HIDE, ewWaitUntilTerminated, Code) then
                    Log(Format('The installed PoolWatch answered --stop-running with exit code %d: the files decide.', [Code]))
                  else
                    Log(Format('The installed PoolWatch could not be started to ask it to exit (error %d): the files decide.', [Code]));
                  WaitForApp(App, RunningWait);
                finally
                  HideWaitPage;
                end;
              end;
              WhenToReplace(App);
            end;
            """), Whole("PrepareToInstall"));
        Assert.Equal(OneLine("""
            procedure KeepFolder(const Folder: String);
            var
              Keep: String;
              N: Integer;
            begin
              if not GoesAtRestart(Folder) then
                Exit;
              Keep := Folder + '\' + KeepName;
              ForceDirectories(Folder);
              if SaveStringToFile(Keep, '', False) then
              begin
                N := GetArrayLength(Keeps);
                SetArrayLength(Keeps, N + 1);
                Keeps[N] := Keep;
                Log('An earlier uninstall gave Windows ' + Folder + ' to delete at its next restart, before the moves into it: ' + Keep +
                  ' keeps it there until after the moves.');
              end
              else
              begin
                NotScheduled := NotScheduled + 1;
                Log('Could not make ' + Keep + ', so at the next restart Windows would delete ' + Folder + ' before the moves into it.');
              end;
            end;
            """), Whole("KeepFolder"));
        Assert.Equal(OneLine("""
            procedure ScheduleMoves(const Dir, Target: String);
            var
              Find: TFindRec;
              Name: String;
            begin
              KeepFolder(Target);
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  Name := Find.Name;
                  if (Name <> '.') and (Name <> '..') then
                  begin
                    if (Find.Attributes and AttrFolder) <> 0 then
                      ScheduleMoves(Dir + '\' + Name, Target + '\' + Name)
                    else
                      try
                        ForceDirectories(Target);
                        RestartReplace(Dir + '\' + Name, Target + '\' + Name);
                        Log('At the next restart Windows moves ' + Dir + '\' + Name + ' to ' + Target + '\' + Name + '.');
                      except
                        NotScheduled := NotScheduled + 1;
                        Log('Windows could not be given ' + Dir + '\' + Name + ' to move at the next restart: ' + GetExceptionMessage);
                      end;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              try
                RestartReplace(Dir, '');
              except
                Log('Windows could not be given the folder ' + Dir + ' to delete at the next restart: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("ScheduleMoves"));
        Assert.Equal(OneLine("""
            function KeepLink(const App: String): Boolean;
            var
              Link, Saved: String;
            begin
              Result := False;
              Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
              if not FileExists(Link) or not GoesAtRestart(Link) then
                Exit;
              Saved := App + '\' + StageFolder + '\' + RunStamp + '.lnk';
              try
                ForceDirectories(App + '\' + StageFolder);
                if not FileCopy(Link, Saved, False) then
                  RaiseException('it could not be copied to ' + Saved);
                RestartReplace(Saved, Link);
                Result := True;
                Log('An earlier uninstall gave Windows the Start menu entry to delete at its next restart: Windows moves a copy of ' +
                  'the new one back after that, from ' + Saved + '.');
              except
                Log('The Start menu entry goes at the next restart, where an earlier uninstall gave Windows it to delete, and ' +
                  'could not be kept: ' + GetExceptionMessage + '. Run the setup again after that restart.');
              end;
            end;
            """), Whole("KeepLink"));
        Assert.Equal(OneLine("""
            procedure SwapAtRestart;
            var
              App: String;
              I: Integer;
            begin
              App := ExpandConstant('{app}');
              NotScheduled := 0;
              ScheduleMoves(StageDir, App);
              KeepLink(App);
              for I := 0 to GetArrayLength(Keeps) - 1 do
                try
                  RestartReplace(Keeps[I], '');
                except
                  Log('Windows could not be given ' + Keeps[I] + ' to delete at the next restart: ' + GetExceptionMessage);
                end;
              try
                RestartReplace(App + '\' + StageFolder, '');
              except
                Log('Windows could not be given the folder ' + App + '\' + StageFolder + ' to delete at the next restart: ' + GetExceptionMessage);
              end;
              if NotScheduled > 0 then
              begin
                Log(Format('Windows could not be given %d of the new files to move at the next restart, so none of them is moved: ' +
                  'the new files in %s are deleted.', [NotScheduled, StageDir]));
                if not DelTree(StageDir, True, True, True) then
                begin
                  Log('Some of the new files could not be deleted: ' + StageDir);
                  Unstage(StageDir, App);
                  if Unkept > 0 then
                    KeepLauncherOut(App);
                end;
                for I := 0 to GetArrayLength(Keeps) - 1 do
                  DeleteFile(Keeps[I]);
              end;
            end;
            """), Whole("SwapAtRestart"));
        Assert.Equal(OneLine("""
            function FinishHeading: String;
            begin
              if NotScheduled > 0 then
                Result := 'PoolWatch is not fully updated'
              else
                Result := 'PoolWatch updates when Windows restarts';
            end;
            """), Whole("FinishHeading"));
        Assert.Equal(OneLine("""
            function FinishText: String;
            var
              Meanwhile: String;
            begin
              Meanwhile := 'until then ' + UntilRestart(ExpandConstant('{app}'));
              if NotScheduled > 0 then
              begin
                Result := 'Windows could not be given the new files to put in place at its next restart, so Setup took them back; ' +
                  Meanwhile + '.';
                if Unkept > 0 then
                begin
                  if Unkept = 1 then
                    Result := Result + ' But one of them, which another program has open (listed below), could not be taken back: ' +
                      'Windows still puts it in place at that restart, among the files of the version you had.'
                  else
                    Result := Result + Format(' But %d of them, which another program has open (listed below), could not be taken ' +
                      'back: Windows still puts those in place at that restart, among the files of the version you had.', [Unkept]);
                  if TakenOut = 1 then
                    Result := Result + ' Setup set one file of the version you had aside now (listed below), as it could keep no ' +
                      'copy of it, so that the restart does not replace it.'
                  else if TakenOut > 1 then
                    Result := Result + Format(' Setup set %d files of the version you had aside now (listed below), as it could ' +
                      'keep no copy of them, so that the restart does not replace them.', [TakenOut]);
                  if LauncherKept > 0 then
                    Result := Result + ' Another program also keeps one of the files that start PoolWatch from being set aside, ' +
                      'and Windows would not take it to set aside at that restart either (listed below), so after that restart ' +
                      'PoolWatch may start from old and new files mixed until the setup has run again.'
                  else
                    Result := Result + ' So that PoolWatch never starts from old and new files mixed, Setup set aside the files ' +
                      'that start it, now or, where another program has one open, at that restart: after that restart PoolWatch ' +
                      'does not start until the setup has run again.';
                end;
                if Refused > 0 then
                  Result := Result + ' Windows would not take every old file Setup could not put back now to put back at that ' +
                    'restart either, so PoolWatch may not start after it until the setup has run again.';
                Result := Result + ' Until the setup has run again, Apps > Installed apps shows this version''s number. Restart Windows, ' +
                  'then run the setup again: it then puts the whole version in place. Its log says what happened.' + #13#10#13#10 +
                  FinishReason;
              end
              else
                Result := 'Windows puts this version of PoolWatch in place the next time it restarts; ' + Meanwhile + '. Setup does ' +
                  'not restart Windows: restart it when it suits you.' + #13#10#13#10 + FinishReason;
            end;
            """), Whole("FinishText"));
        Assert.Equal(OneLine("""
            procedure SweepUninstallers(const App: String);
            var
              Find: TFindRec;
              Path, Mine: String;
            begin
              if UninstallerDir = '' then
                Exit;
              Mine := ChangeFileExt(ExtractFileName(ExpandConstant('{uninstallexe}')), '');
              if FindFirst(App + '\unins*', Find) then
              try
                repeat
                  Path := App + '\' + Find.Name;
                  if ((Find.Attributes and AttrFolder) = 0) and
                     ((CompareText(UninstallerDir, App) <> 0) or (CompareText(ChangeFileExt(Find.Name, ''), Mine) <> 0)) then
                    if DeleteFile(Path) then
                      Log('Deleted ' + Path + ', which the uninstaller ' + ExpandConstant('{uninstallexe}') + ' replaces.')
                    else
                      Log('In use, so not deleted now' + HoldersOf(Path) + ': ' + Path + '. A later setup or the uninstall deletes it.');
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              if FindFirst(App + '\' + UninstallFolder + '\*', Find) then
              try
                repeat
                  Path := App + '\' + UninstallFolder + '\' + Find.Name;
                  if (Find.Name <> '.') and (Find.Name <> '..') and (CompareText(Path, UninstallerDir) <> 0) then
                    if DelTree(Path, True, True, True) then
                      Log('Deleted ' + Path + ', an earlier setup''s uninstaller.')
                    else
                      Log('Not all of ' + Path + ', an earlier setup''s uninstaller, could be deleted now: a later setup or the uninstall deletes it.');
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              RemoveDir(App + '\' + UninstallFolder);
            end;
            """), Whole("SweepUninstallers"));
        Assert.Equal(OneLine("""
            procedure CurStepChanged(CurStep: TSetupStep);
            var
              App: String;
            begin
              App := ExpandConstant('{app}');
              if CurStep = ssInstall then
              begin
                InstallBegun := True;
                OldUninstaller := LauncherPath(App) = '';
                SaveLinkBefore;
                SaveEntryBefore;
                SetAllAside(App);
                SaveUninstallerBefore(App);
              end
              else if CurStep = ssPostInstall then
              begin
                InstallDone := True;
                if FinishAtRestart then
                begin
                  SwapAtRestart;
                  Log(FinishHeading + '. ' + FinishText + FinishFiles);
                  if NotScheduled = 0 then
                    RemoveOldLayout(App, True);
                end
                else
                begin
                  DropAside;
                  RemoveOldLayout(App, False);
                  if KeepLink(App) then
                    try
                      RestartReplace(App + '\' + StageFolder, '');
                    except
                      Log('Windows could not be given the folder ' + App + '\' + StageFolder + ' to delete at the next restart: ' + GetExceptionMessage);
                    end;
                end;
                SweepUninstallers(App);
              end;
            end;
            """), Whole("CurStepChanged"));
        Assert.Equal(OneLine("""
            function GetCustomSetupExitCode: Integer;
            begin
              Result := 0;
              if FinishAtRestart then
              begin
                if NotScheduled > 0 then
                  Result := ExitNotFinished
                else
                  Result := ExitFinishesAtRestart;
              end;
            end;
            """), Whole("GetCustomSetupExitCode"));
        Assert.Equal(OneLine("""
            function CleanupDone(Code: Integer): Boolean;
            begin
              Result := (Code = 0) or (Code = ExitNotAllPutBack) or (Code = ExitOtherDataFolder);
            end;
            """), Whole("CleanupDone"));
        Assert.Equal(OneLine("""
            function NotRemovedText(Code: Integer): String;
            begin
              if Code = ExitStillRunning then
                Result := 'PoolWatch is still running, so it put nothing back and the uninstaller has removed nothing. Close it ' +
                  'from its tray icon (right-click it, choose Exit), or wait a minute, then click Retry.'
              else if Code = -1 then
                Result := 'PoolWatch could not be started for its cleanup, so it put nothing back and the uninstaller has removed ' +
                  'nothing. Click Retry to try again. If this keeps happening, run PoolWatch''s setup again to repair it, then ' +
                  'uninstall it.'
              else if Code = NotInPlace then
              begin
                if RestartWorkPending(ExpandConstant('{app}')) then
                  Result := 'An update of PoolWatch, a setup of it that did not finish, or an earlier uninstall finishes when Windows ' +
                    'next restarts, so its files are not in place yet: PoolWatch put nothing back and the uninstaller has removed ' +
                    'nothing. Restart Windows, then uninstall PoolWatch.'
                else
                  Result := 'PoolWatch''s program, ' + ProgramPath(ExpandConstant('{app}')) + ', is not there, or the launcher beside it ' +
                    'is of another version, so PoolWatch put nothing back and the uninstaller has removed nothing. Run PoolWatch''s ' +
                    'setup again to repair it, then uninstall it.';
              end
              else if Code = ExitNotStarted then
                Result := 'PoolWatch''s launcher could not start PoolWatch for its cleanup (error 10), so it put nothing back and ' +
                  'the uninstaller has removed nothing. Click Retry to try again. If this keeps happening, run PoolWatch''s setup ' +
                  'again to repair it, then uninstall it.'
              else if Code = ExitRefused then
                Result := 'PoolWatch refused to start its cleanup (error 11: it was started without administrator rights, or with ' +
                  '.NET''s start-up variables set), so it put nothing back and the uninstaller has removed nothing. Click Retry to ' +
                  'try again. If this keeps happening, run PoolWatch''s setup again to repair it, then uninstall it.'
              else if Code = ExitBadCommandLine then
                Result := 'PoolWatch did not take the command line of its cleanup (error 2), so it put nothing back and the ' +
                  'uninstaller has removed nothing. Click Cancel, run PoolWatch''s setup again to repair it, then uninstall it.'
              else if Code = ExitNotInAdminFolder then
                Result := 'PoolWatch did not run its cleanup, because someone who is not an administrator could change its program, ' +
                  'its launcher or a folder they are in (error 5), so it put nothing back and the uninstaller has removed nothing. ' +
                  'Click Cancel, make sure only administrators can change ' + ExpandConstant('{app}') + ' and the files in it, then ' +
                  'uninstall PoolWatch again.'
              else if Code = ExitDataNotSafe then
                Result := 'PoolWatch could not make its data folder (' + ExpandConstant('{commonappdata}\PoolWatch') + ') safe to ' +
                  'use, so it put nothing back and the uninstaller has removed nothing. Close any program that may have that folder ' +
                  'open, then click Retry. If this keeps happening, restart Windows, then uninstall PoolWatch again.'
              else if Code = ExitNamesNotSafe then
                Result := 'PoolWatch could not open its private names, so it could not ask a running PoolWatch to exit: it put ' +
                  'nothing back and the uninstaller has removed nothing. Click Retry to try again. If this keeps happening, restart ' +
                  'Windows, then uninstall PoolWatch again.'
              else if Code = ExitFailed then
                Result := 'An error stopped PoolWatch''s cleanup (error 1). It may have put back some of its changes, and the ' +
                  'uninstaller cannot tell which, so it has removed nothing. Click Retry to try again. If this keeps happening, run ' +
                  'PoolWatch''s setup again to repair it, then uninstall it.'
              else
                Result := Format('PoolWatch''s cleanup failed (error %d). It may have put back some of its changes, and the ' +
                  'uninstaller cannot tell which, so it has removed nothing. Click Retry to try again. If this keeps happening, run ' +
                  'PoolWatch''s setup again to repair it, then uninstall it.', [Code]);
            end;
            """), Whole("NotRemovedText"));
        Assert.Equal(OneLine("""
            function IsLink(const Path: String): Boolean;
            var
              Find: TFindRec;
            begin
              Result := False;
              if FindFirst(Path, Find) then
              try
                Result := (Find.Attributes and AttrLink) <> 0;
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("IsLink"));
        Assert.Equal(OneLine("""
            procedure KeepOut(const Dir, App, Rel: String);
            var
              Find: TFindRec;
              Name, Target: String;
            begin
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  Name := Find.Name;
                  if (Name <> '.') and (Name <> '..') and ((Find.Attributes and AttrLink) = 0) then
                  begin
                    if (Find.Attributes and AttrFolder) <> 0 then
                      KeepOut(Dir + '\' + Name, App, Rel + Name + '\')
                    else
                    begin
                      Target := App + '\' + Rel + Name;
                      if not FileExists(Target) then
                      begin
                        ForceDirectories(ExtractFileDir(Target));
                        if SaveStringToFile(Target, '', False) then
                          Log('Still in use: ' + Dir + '\' + Name + ', which an earlier setup gave Windows to move to ' + Target +
                            ' at the next restart. ' + Target + ' is made now, so that Windows deletes it after that move.')
                        else
                        begin
                          RestartStays := RestartStays + 1;
                          Log('May stay after the next restart: ' + Target + ', if an earlier setup gave Windows ' + Dir + '\' +
                            Name + ' (still in use) to move there at that restart: ' + Target + ' could not be made now, so ' +
                            'Windows is not given it to delete after such a move.');
                        end;
                      end;
                    end;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("KeepOut"));
        Assert.Equal(OneLine("""
            procedure KeepOutAtRestart(const App: String);
            var
              Find: TFindRec;
            begin
              if FindFirst(App + '\' + StageFolder + '\*', Find) then
              try
                repeat
                  if (Find.Name <> '.') and (Find.Name <> '..') and ((Find.Attributes and AttrFolder) <> 0) and
                     ((Find.Attributes and AttrLink) = 0) then
                    KeepOut(App + '\' + StageFolder + '\' + Find.Name, App, '');
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("KeepOutAtRestart"));
        Assert.Equal(OneLine("""
            procedure DeleteOneAtRestart(const Path, Shown, Holders: String);
            begin
              try
                RestartReplace(Path, '');
                RestartGiven := RestartGiven + 1;
                if Shown <> '' then
                  AddShown(RestartList, RestartCount, Shown + Holders);
                Log('Windows deletes it at its next restart: ' + Path + Holders);
              except
                RestartFailed := RestartFailed + 1;
                Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("DeleteOneAtRestart"));
        Assert.Equal(OneLine("""
            procedure DeleteAtRestart(const Dir, Rel: String);
            var
              Find: TFindRec;
              Name: String;
            begin
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  Name := Find.Name;
                  if (Name <> '.') and (Name <> '..') then
                  begin
                    if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then
                      DeleteAtRestart(Dir + '\' + Name, Rel + Name + '\')
                    else
                      DeleteOneAtRestart(Dir + '\' + Name, Rel + Name, HoldersOf(Dir + '\' + Name));
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              DeleteOneAtRestart(Dir, '', '');
            end;
            """), Whole("DeleteAtRestart"));
        Assert.Equal(OneLine("""
            procedure RemoveRest(const App: String);
            var
              Link: String;
            begin
              RestartList := '';
              RestartCount := 0;
              RestartGiven := 0;
              RestartStays := 0;
              RestartFailed := 0;
              Accounts := '';
              if (CompareText(App, ExpandConstant('{autopf}\PoolWatch')) = 0) and DirExists(App) and not IsLink(App) then
              begin
                DelTree(App, True, True, True);
                if DirExists(App + '\' + StageFolder) then
                  KeepOutAtRestart(App);
                if DirExists(App) then
                  DeleteAtRestart(App, '');
              end;
              Link := ExpandConstant('{autoprograms}\PoolWatch.lnk');
              if FileExists(Link) then
                if not DeleteFile(Link) then
                  DeleteOneAtRestart(Link, Link, HoldersOf(Link));
            end;
            """), Whole("RemoveRest"));
        Assert.Equal(OneLine("""
            function RestartText(const App: String): String;
            var
              FolderGoes: Boolean;
            begin
              FolderGoes := (RestartFailed + RestartStays = 0) and (RestartGiven > RestartCount);
              Result := 'Some of PoolWatch''s files or folders could not be removed now: another program has them in use, or an ' +
                'earlier update of PoolWatch finishes at the next restart.';
              if (RestartCount > 0) and FolderGoes then
                Result := Result + ' Windows deletes them, and the folder ' + App + ', when it next restarts:' + RestartList +
                  MoreLine(RestartCount)
              else if RestartCount > 0 then
                Result := Result + ' Windows deletes these when it next restarts:' + RestartList + MoreLine(RestartCount)
              else if FolderGoes then
                Result := Result + ' Windows deletes the folder ' + App + ', with the folders left in it, when it next restarts.';
              if (RestartCount > MaxShown) and (ExpandConstant('{log}') <> '') then
                Result := Result + ' The uninstaller''s log, ' + ExpandConstant('{log}') + ', names each of them.';
              if RestartFailed > 0 then
                Result := Result + #13#10#13#10 + Format('The uninstaller could not give Windows %d of PoolWatch''s files or ' +
                  'folders to delete at the restart.', [RestartFailed]);
              if RestartStays > 0 then
                Result := Result + #13#10#13#10 + Format('An earlier update of PoolWatch may have Windows move %d of its files ' +
                  'into %s at the restart, where they would stay after it: the uninstaller could not make their place ready for ' +
                  'Windows to delete after the move.', [RestartStays, App]);
              if RestartFailed + RestartStays > 0 then
                Result := Result + ' After the restart, delete what is left of ' + App + ', and PoolWatch''s Start menu entry if ' +
                  'it is still there.';
              Result := Result + #13#10#13#10 + 'The uninstaller does not restart Windows: restart it when it suits you.';
            end;
            """), Whole("RestartText"));
        // A silent uninstall that leaves anything for the restart still ends with exit code 0 (Inno Setup's uninstaller
        // lets its script set no other), so its log says so, and how a script that started it with /LOG=<file> tells: by
        // the words DeleteOneAtRestart and KeepOut start their messages with, held here to be the same, which follow the
        // time stamp Inno Setup's log puts before each message. That the uninstall finishes at the restart is said only
        // when Windows was given everything left and no file an earlier update may have Windows move into the install
        // folder then would stay; what it could not be given, and such a file, are each said on their own.
        Assert.Equal(OneLine("""
            function RestartLogNote: String;
            begin
              if RestartFailed > 0 then
                Result := 'This uninstall does not finish at the next restart of Windows: Windows deletes then only what it was ' +
                  'given, and what it could not be given stays after that restart, to delete by hand.'
              else if RestartStays > 0 then
                Result := 'This uninstall may not finish at the next restart of Windows: Windows deletes then what it was given, ' +
                  'but a file an earlier update of PoolWatch may have it move into the install folder then would stay, to delete ' +
                  'by hand.'
              else
                Result := 'This uninstall finishes at the next restart of Windows, which deletes what is named above.';
              Result := Result + ' Its exit code is 0 all the same: Inno Setup''s uninstaller lets its script set no other. A ' +
                'script that started it with /LOG=<file> can tell from that log, which puts a time stamp before each message: ' +
                'each file or folder left for the restart is named in a message that starts ' +
                '"Windows deletes it at its next restart: ", each thing Windows could not be given in one that starts ' +
                '"Windows could not be given ", and each file that may stay in one that starts ' +
                '"May stay after the next restart: ".';
            end;
            """), Whole("RestartLogNote"));
        string giveOne = Whole("DeleteOneAtRestart");
        Assert.Contains("Log('Windows deletes it at its next restart: ' + Path + Holders);", giveOne);
        Assert.Contains("Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);", giveOne);
        // The words the note quotes (read with its string literals joined, as the message reads) are those the messages of
        // DeleteOneAtRestart and KeepOut start with, and no line of the note or the notice starts with them (Inno Setup's
        // Log indents each further line of a message to where the message starts): not one RestartText or RestartLogNote
        // starts, nor a line of the list (AddShown) or the one that counts the rest (MoreLine). So a script that looks for
        // them after the time stamp finds only those messages.
        string note = JoinedLiterals(Whole("RestartLogNote"));
        var lineStarts = new[] { "RestartText", "RestartLogNote", "AddShown", "MoreLine" }.SelectMany(r => LineStarts(Whole(r)))
            .Where(s => s.Length > 0).ToList(); // MoreLine's '' when there is no more starts no line
        // The starts are read: each paragraph of the notice, the note, a list line and the count of the rest.
        Assert.Equal(["Some of PoolWatch's files or folders could not be removed now: another program has them in use, or an earlier update of PoolWatch finishes at the next restart.",
            "The uninstaller could not give Windows %d of PoolWatch's files or folders to delete at the restart.",
            "An earlier update of PoolWatch may have Windows move %d of its files into %s at the restart, where they would stay after it: the uninstaller could not make their place ready for Windows to delete after the move.",
            "The uninstaller does not restart Windows: restart it when it suits you.",
            "This uninstall does not finish at the next restart of Windows: Windows deletes then only what it was given, and what it could not be given stays after that restart, to delete by hand.",
            "This uninstall may not finish at the next restart of Windows: Windows deletes then what it was given, but a file an earlier update of PoolWatch may have it move into the install folder then would stay, to delete by hand.",
            "This uninstall finishes at the next restart of Windows, which deletes what is named above.", "- ", "And %d more."],
            lineStarts.Select(s => s.Replace("''", "'")));
        foreach (var (words, routine) in new[] { ("Windows deletes it at its next restart: ", "DeleteOneAtRestart"),
                     ("Windows could not be given ", "DeleteOneAtRestart"), ("May stay after the next restart: ", "KeepOut") })
        {
            Assert.Contains($"starts \"{words}\"", note);
            Assert.Matches($@"Log\('{Regex.Escape(words)}' \+ (Path|Target) \+", Whole(routine));
            Assert.DoesNotContain(lineStarts, s => s.StartsWith(words, StringComparison.Ordinal));
        }
        Assert.DoesNotContain("on a line that starts", note); // each line of the log starts with its time stamp
        // SECURITY.md and the user guide say the same: the words the note quotes, each, the case in which the uninstall
        // does not finish at the restart and the one in which it may not, and that the uninstaller's second copy runs from
        // a folder of its own in TEMP (Inno Setup 6.7.3, Setup.Uninstall.pas, lines 409 and 410: CreateTempDir).
        var quoted = Regex.Matches(note, "starts \"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Windows deletes it at its next restart: ", "Windows could not be given ", "May stay after the next restart: "], quoted);
        foreach (string doc in new[] { Source("SECURITY.md"), Source("docs", "USER-GUIDE.md") })
        {
            string said = OneLine(doc);
            Assert.All(quoted, words => Assert.Contains($"starts \"{words}\"", said));
            Assert.Contains("it does not when Windows could not be given some of what is left, which then stays, to delete by hand", said);
            Assert.Contains("it may not when the uninstaller could not make ready the place of a file an earlier update", said);
            Assert.Contains("each file or folder left for", said);
            Assert.Matches(@"`_unins\.tmp`,? in a folder of its own in TEMP", said);
            Assert.DoesNotMatch(@"`_unins\.tmp`,? in TEMP", said);
        }
        Assert.Equal(OneLine("""
            procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
            var
              KeepChanges, Summarized, RanAgain, Again: Boolean;
              Code, Seconds: Integer;
              DataDir, App: String;
            begin
              DataDir := ExpandConstant('{commonappdata}\PoolWatch');
              App := ExpandConstant('{app}');
              case CurUninstallStep of
                { After "Are you sure?" and before any file is removed: Abort here ends the uninstall with nothing removed. }
                usAppMutexCheck:
                  begin
                    { The list is made before the cleanup stops PoolWatch, so it can differ from what the cleanup then finds (a change
                      made or put back in between): it is only shown and logged. What was really put back is uninstall-summary.txt,
                      which every notice after the cleanup reads. The choice is logged as what is to be done: the cleanup has not
                      run yet, and a change PoolWatch cannot put back then stays. }
                    ReadChangeList(DataDir);
                    KeepChanges := KeepChangesWanted;
                    if KeepChanges then
                      Log('The changes are to be kept: only the logon task and the desktop shortcut are to go.')
                    else
                      Log('Every change is to be put back (one PoolWatch cannot put back stays).');
                    { No file is removed unless the cleanup did what it was asked (CleanupDone: 0 or 8). A cleanup that failed may
                      have put back only part of the changes, and the uninstaller cannot tell which, so removing the files that could
                      put back the rest would lose them. A silent uninstall then ends with nothing removed; one started by hand says
                      why and offers Retry, which runs the cleanup again (adding to uninstall-summary.txt once a cleanup of this
                      uninstall wrote it). Once the cleanup is done, the uninstaller waits until no file of PoolWatch's program in
                      the install folder is in use, for as long as a process with PoolWatch's name runs from it (WaitForApp: a
                      silent uninstall up to RunningWait, one started by hand HandWait, as it has no page that keeps drawing). If
                      part of PoolWatch may still run then (a file of it is still in use, and a process with its name runs or may
                      run), the cleanup runs once more, adding to uninstall-summary.txt: it is to stop a PoolWatch started since the
                      first (whose service profile can apply at its start) and to undo what that one changed (with the changes
                      kept, only by removing the logon task and the desktop shortcut, as in the first run). Whatever still has a file
                      in use after that does not keep the uninstall from going on: what is still in use when the files are removed
                      is given to Windows to delete at the next restart, and what Windows could not be given is said (RemoveRest,
                      RestartText). Each line logged here before the result it names says what is to be done, as that result is
                      not known yet. }
                    Code := RunCleanup(KeepChanges, False);
                    Summarized := False;
                    RanAgain := False;
                    if UninstallSilent then
                      Seconds := RunningWait
                    else
                      Seconds := HandWait;
                    repeat
                      while not CleanupDone(Code) do
                      begin
                        Log(NotRemovedText(Code));
                        if UninstallSilent then
                          Abort;
                        if MsgBox(NotRemovedText(Code), mbError, MB_RETRYCANCEL) <> IDRETRY then
                          Abort;
                        Code := RunCleanup(KeepChanges, Summarized);
                      end;
                      Summarized := True;
                      AppCount := WaitForApp(App, Seconds);
                      Again := (AppCount <> 0) and not RanAgain;
                      if Again then
                      begin
                        RanAgain := True;
                        Seconds := HandWait;
                        Log('Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more: it is to stop a ' +
                          'PoolWatch started since and to undo what that one changed (when the changes are to be kept, only to remove the ' +
                          'logon task and the desktop shortcut).');
                        Code := RunCleanup(KeepChanges, True);
                      end;
                    until not Again;
                    if AppCount <> 0 then
                      Log('Part of PoolWatch may still run (' + RunningNames + '): what it still has in use when the uninstaller ' +
                        'comes to remove the files is to be given to Windows to delete at the next restart.');
                    ChangesPutBack := (Code = 0) and not KeepChanges;
                    if not UninstallSilent then
                    begin
                      if Code = ExitNotAllPutBack then
                        MsgBox(NotAllPutBack(DataDir), mbInformation, MB_OK)
                      else if (Code = ExitOtherDataFolder) and KeepChanges then
                        MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' +
                          'starts with PoolWatch.safe-). Your changes were kept: their list stays in that folder, so PoolWatch can still ' +
                          'put them back if you install it again. Neither folder is deleted: check them, then delete them yourself.',
                          mbInformation, MB_OK)
                      else if Code = ExitOtherDataFolder then
                        MsgBox('PoolWatch could not use ' + DataDir + ', so it kept its data in a folder of its own next to it (its name ' +
                          'starts with PoolWatch.safe-). PoolWatch put back what it could from there; uninstall-summary.txt in that folder ' +
                          'says what was put back and what was not, and why. Neither folder is deleted: check them, then delete them ' +
                          'yourself.', mbInformation, MB_OK)
                      else if KeepChanges and (PutBackChoice = ChoiceAsk) then
                        MsgBox('Your changes were kept. Their list stays in ' + DataDir + ', so PoolWatch can still put them back ' +
                          'if you install it again.', mbInformation, MB_OK);
                    end;
                  end;
                usPostUninstall:
                  begin
                    RemoveRest(App);
                    if RestartGiven + RestartFailed + RestartStays > 0 then
                    begin
                      Log(RestartText(App));
                      Log(RestartLogNote);
                      if not UninstallSilent then
                        MsgBox(RestartText(App), mbInformation, MB_OK);
                    end;
                    if ChangesPutBack then
                      if DeleteDataWanted(DataDir) then
                      begin
                        DelTree(DataDir, True, True, True);
                        SayDataLeft(DataDir);
                      end;
                  end;
              end;
            end;
            """), Whole("CurUninstallStepChanged"));
        Assert.Equal(OneLine("""
            function FreeSoon(const Path: String): Boolean;
            var
              Quarter: Integer;
            begin
              Result := CanReplace(Path);
              Quarter := 0;
              while not Result and (Quarter < BriefHold * 4) do
              begin
                Sleep(250);
                Quarter := Quarter + 1;
                Result := CanReplace(Path);
              end;
            end;
            """), Whole("FreeSoon"));
        Assert.Equal(OneLine("""
            function StartsPoolWatch(const Path: String): Boolean;
            begin
              Result := (CompareText(Path, ExpandConstant('{app}') + '\' + PoolWatchName) = 0) or
                (CompareText(Path, SecondLauncherPath(ExpandConstant('{app}'))) = 0);
            end;
            """), Whole("StartsPoolWatch"));
        Assert.Equal(OneLine("""
            procedure MoveBackAtRestart(const Target, Aside: String);
            begin
              if not FileExists(Aside) then
                Exit;
              try
                RestartReplace(Aside, Target);
                Log('Windows puts ' + Target + ' back at its next restart, from ' + Aside + '.');
              except
                Refused := Refused + 1;
                Log('Windows could not be given ' + Aside + ' to put back at its next restart, so it stays set aside: ' +
                  GetExceptionMessage);
              end;
            end;
            """), Whole("MoveBackAtRestart"));
        Assert.Equal(OneLine("""
            procedure SaveEntryValue(const Name, Text: String; Number: Cardinal; IsNumber: Boolean);
            var
              N: Integer;
            begin
              N := GetArrayLength(EntryNames);
              SetArrayLength(EntryNames, N + 1);
              SetArrayLength(EntryText, N + 1);
              SetArrayLength(EntryNumber, N + 1);
              SetArrayLength(EntryIsNumber, N + 1);
              EntryNames[N] := Name;
              EntryText[N] := Text;
              EntryNumber[N] := Number;
              EntryIsNumber[N] := IsNumber;
            end;
            """), Whole("SaveEntryValue"));
        Assert.Equal(OneLine("""
            procedure SaveEntryBefore;
            var
              Names: TArrayOfString;
              Text: String;
              Number: Cardinal;
              I: Integer;
            begin
              EntryNew := not RegKeyExists(HKLM, UninstallEntryKey);
              EntrySaved := RegGetValueNames(HKLM, UninstallEntryKey, Names);
              if not EntrySaved then
                Exit;
              EntryCount := GetArrayLength(Names);
              for I := 0 to GetArrayLength(Names) - 1 do
                if RegQueryStringValue(HKLM, UninstallEntryKey, Names[I], Text) then
                  SaveEntryValue(Names[I], Text, 0, False)
                else if RegQueryDWordValue(HKLM, UninstallEntryKey, Names[I], Number) then
                  SaveEntryValue(Names[I], '', Number, True)
                else
                  Log('A value of the Apps and Features entry is neither text nor a number, so a setup that does not finish cannot put it back: ' + Names[I]);
            end;
            """), Whole("SaveEntryBefore"));
        Assert.Equal(OneLine("""
            procedure PutEntryBack;
            var
              I: Integer;
              Done: Boolean;
            begin
              if EntryNew then
              begin
                if RegKeyExists(HKLM, UninstallEntryKey) then
                  if RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey) then
                    Log('Deleted what Setup wrote of an Apps and Features entry, where there was none before the setup.')
                  else
                    Log('Could not delete what Setup wrote of an Apps and Features entry, ' + UninstallEntryKey + '.');
                Exit;
              end;
              if not EntrySaved or EntryAsSaved then
                Exit;
              Done := True;
              if RegKeyExists(HKLM, UninstallEntryKey) then
                Done := RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey);
              for I := 0 to GetArrayLength(EntryNames) - 1 do
                if EntryIsNumber[I] then
                  Done := RegWriteDWordValue(HKLM, UninstallEntryKey, EntryNames[I], EntryNumber[I]) and Done
                else
                  Done := RegWriteStringValue(HKLM, UninstallEntryKey, EntryNames[I], EntryText[I]) and Done;
              if Done then
                Log('Put back the Apps and Features entry as it was before the setup.')
              else
                Log('Could not put back every value of the Apps and Features entry, ' + UninstallEntryKey + '.');
            end;
            """), Whole("PutEntryBack"));
        Assert.Equal(OneLine("""
            procedure SaveUninstallerBefore(const App: String);
            var
              Find: TFindRec;
              Ext, Saved: String;
              N: Integer;
            begin
              SetArrayLength(UninstallerBefore, 0);
              if CompareText(UninstallDir(''), App) <> 0 then
                Exit;
              if FindFirst(App + '\unins*', Find) then
              try
                repeat
                  Ext := ExtractFileExt(Find.Name);
                  if ((Find.Attributes and AttrFolder) = 0) and ((CompareText(Ext, '.exe') = 0) or (CompareText(Ext, '.msg') = 0)) then
                  begin
                    Saved := ExpandConstant('{tmp}\uninstaller-before\') + Find.Name;
                    if ForceDirectories(ExtractFileDir(Saved)) and FileCopy(App + '\' + Find.Name, Saved, False) then
                    begin
                      N := GetArrayLength(UninstallerBefore);
                      SetArrayLength(UninstallerBefore, N + 1);
                      UninstallerBefore[N] := Find.Name;
                    end
                    else
                      Log('Could not copy ' + App + '\' + Find.Name + ', so a setup that does not finish cannot put it back.');
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("SaveUninstallerBefore"));
        Assert.Equal(OneLine("""
            procedure PutUninstallerBack;
            var
              I: Integer;
              App, Path, Saved: String;
            begin
              if GetArrayLength(UninstallerBefore) = 0 then
                Exit;
              App := ExpandConstant('{app}');
              for I := 0 to GetArrayLength(UninstallerBefore) - 1 do
              begin
                Path := App + '\' + UninstallerBefore[I];
                Saved := ExpandConstant('{tmp}\uninstaller-before\') + UninstallerBefore[I];
                if FileCopy(Saved, Path, False) then
                  Log('Put back ' + Path + ' as it was before the setup.')
                else
                  try
                    if GetSHA256OfFile(Path) = GetSHA256OfFile(Saved) then
                      Log(Path + ' could not be copied over' + HoldersOf(Path) + ', and is as it was before the setup.')
                    else
                      Log('Could not put back ' + Path + ' as it was before the setup' + HoldersOf(Path) + '.');
                  except
                    Log('Could not put back ' + Path + ' as it was before the setup, nor read it to see whether the setup had ' +
                      'replaced it' + HoldersOf(Path) + ': ' + GetExceptionMessage);
                  end;
              end;
            end;
            """), Whole("PutUninstallerBack"));
        Assert.Equal(OneLine("""
            procedure Shipping;
            var
              N: Integer;
            begin
              N := GetArrayLength(Shipped);
              SetArrayLength(Shipped, N + 1);
              Shipped[N] := ExpandConstant(CurrentFileName);
            end;
            """), Whole("Shipping"));
        Assert.Equal(OneLine("""
            function Ships(const Path: String): Boolean;
            var
              I: Integer;
            begin
              Result := False;
              for I := 0 to GetArrayLength(Shipped) - 1 do
                if CompareText(Shipped[I], Path) = 0 then
                  Result := True;
            end;
            """), Whole("Ships"));
        Assert.Equal(OneLine("""
            procedure Unstage(const Dir, Target: String);
            var
              Find: TFindRec;
              Found: TArrayOfString;
              Name, Path, Place, Saved, Before: String;
              I, N: Integer;
            begin
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  if (Find.Name <> '.') and (Find.Name <> '..') then
                  begin
                    N := GetArrayLength(Found);
                    SetArrayLength(Found, N + 1);
                    if (Find.Attributes and AttrFolder) <> 0 then
                      Found[N] := '\' + Find.Name
                    else
                      Found[N] := Find.Name;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              for I := 0 to GetArrayLength(Found) - 1 do
              begin
                Name := Found[I];
                if Copy(Name, 1, 1) = '\' then
                begin
                  Name := Copy(Name, 2, Length(Name) - 1);
                  Unstage(Dir + '\' + Name, Target + '\' + Name);
                end
                else
                begin
                  Path := Dir + '\' + Name;
                  Place := Target + '\' + Name;
                  if RenameFile(Path, Path + '.unmoved') then
                    DeleteNowOrAtRestart(Path + '.unmoved')
                  else
                  begin
                    Before := '';
                    Saved := '';
                    try
                      Before := FilledAtRestart(Place);
                      if Before <> '' then
                      begin
                        if not FileCopy(Before, Place + '.' + RunStamp + '.old', False) then
                          if not CreateHardLinkW(Place + '.' + RunStamp + '.old', Before, 0) then
                            RaiseException('it could be neither copied nor given a second name, ' + Place + '.' + RunStamp + '.old');
                        Saved := Place + '.' + RunStamp + '.old';
                        RestartReplace(Saved, Place);
                      end
                      else
                        RestartReplace(Place, '');
                      Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so Windows moves it to ' + Place +
                        ' at its next restart, and then puts back what would be there then.');
                    except
                      Unkept := Unkept + 1;
                      FinishFiles := FinishFiles + #13#10 + Place + ', which Windows still moves into place at the restart' + HoldersOf(Path);
                      Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so Windows moves it to ' + Place +
                        ' at its next restart, and what would be there then could not be kept: ' + GetExceptionMessage);
                      if (Saved = '') and (Before <> '') and not StartsPoolWatch(Before) then
                        if RenameFile(Before, Before + '.' + RunStamp + '.out.old') then
                        begin
                          if CompareText(Before, Place) = 0 then
                          begin
                            TakenOut := TakenOut + 1;
                            FinishFiles := FinishFiles + #13#10 + Place + ', which Setup set aside now, as ' + Before + '.' +
                              RunStamp + '.out.old, so that the restart does not replace it';
                            Log('Set aside ' + Place + ' now, as ' + Before + '.' + RunStamp + '.out.old, so that this setup''s ' +
                              'file does not replace it at the restart.');
                          end
                          else
                            Log('Renamed ' + Before + ' aside, so that its move to ' + Place + ' at the restart finds nothing ' +
                              'and this setup''s file does not replace it there.');
                        end;
                    end;
                  end;
                end;
              end;
            end;
            """), Whole("Unstage"));
        Assert.Equal(OneLine("""
            function CreateHardLinkW(FileName, ExistingFileName: String; Reserved: THandle): BOOL;
              external 'CreateHardLinkW@kernel32.dll stdcall';
            """), Whole("CreateHardLinkW"));
        Assert.Equal(OneLine("""
            function SetAsideSoon(const Path, Aside: String): Boolean;
            var
              Quarter: Integer;
            begin
              Result := False;
              Quarter := 0;
              repeat
                if Quarter > 0 then
                  Sleep(250);
                if CanReplace(Path) then
                  Result := RenameFile(Path, Aside);
                Quarter := Quarter + 1;
              until Result or (Quarter > BriefHold * 4);
            end;
            """), Whole("SetAsideSoon"));
        Assert.Equal(OneLine("""
            function EntryAsSaved: Boolean;
            var
              Names: TArrayOfString;
              Text: String;
              Number: Cardinal;
              I: Integer;
            begin
              Result := RegGetValueNames(HKLM, UninstallEntryKey, Names) and (GetArrayLength(Names) = EntryCount);
              for I := 0 to GetArrayLength(EntryNames) - 1 do
                if Result then
                begin
                  if EntryIsNumber[I] then
                    Result := RegQueryDWordValue(HKLM, UninstallEntryKey, EntryNames[I], Number) and (Number = EntryNumber[I])
                  else
                    Result := RegQueryStringValue(HKLM, UninstallEntryKey, EntryNames[I], Text) and (Text = EntryText[I]);
                end;
            end;
            """), Whole("EntryAsSaved"));
        Assert.Equal(OneLine("""
            function TakeBack(const Path: String): Boolean;
            var
              Moved: String;
            begin
              Result := True;
              if not FileExists(Path) then
                Exit;
              Moved := Path + '.' + RunStamp + '.new';
              if not RenameFile(Path, Moved) then
              begin
                Result := False;
                Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so it stays until the next restart, and no old ' +
                  'file goes back before then.');
                try
                  RestartReplace(Path, '');
                  Log('Windows deletes ' + Path + ' at its next restart, before it puts the old files back.');
                except
                  Refused := Refused + 1;
                  LeftNew := LeftNew + Lowercase(Path) + #0;
                  Log('Windows could not be given ' + Path + ' to delete at its next restart, so it stays after it too: ' +
                    GetExceptionMessage);
                end;
              end
              else if DeleteFile(Moved) then
                Log('Took back ' + Path + '.')
              else
              begin
                Result := False;
                Log('Could not delete ' + Moved + ' now' + HoldersOf(Moved) + ', so no old file goes back before the next ' +
                  'restart.');
                try
                  RestartReplace(Moved, '');
                  Log('Windows deletes ' + Moved + ' at its next restart.');
                except
                  Refused := Refused + 1;
                  Log('Windows could not be given ' + Moved + ' to delete at its next restart, so it stays after it too: ' +
                    GetExceptionMessage);
                end;
              end;
            end;
            """), Whole("TakeBack"));
        Assert.Equal(OneLine("""
            function HoldOldUninstaller(const App: String): Boolean;
            var
              Find: TFindRec;
              Found: TArrayOfString;
              Path, MsgFile, ExeFile: String;
              I, N: Integer;
              Kept, HasMsg: Boolean;
            begin
              Result := True;
              if FindFirst(App + '\unins???.dat', Find) then
              try
                repeat
                  if (Find.Attributes and AttrFolder) = 0 then
                  begin
                    N := GetArrayLength(Found);
                    SetArrayLength(Found, N + 1);
                    Found[N] := Find.Name;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
              for I := 0 to GetArrayLength(Found) - 1 do
              begin
                Path := App + '\' + Found[I];
                MsgFile := ChangeFileExt(Path, '.msg');
                ExeFile := ChangeFileExt(Path, '.exe');
                HasMsg := FileExists(MsgFile);
                Kept := HoldUntilRestart(Path);
                if Kept then
                  Log('Until the next restart the uninstaller there finds no record, ' + Path + ', and removes nothing.')
                else if HasMsg then
                begin
                  Kept := HoldUntilRestart(MsgFile);
                  if Kept then
                    Log('Until the next restart the uninstaller there finds no messages, ' + MsgFile + ', and removes nothing.');
                end
                else
                  Log(Path + ' could not be set aside until the next restart, and that uninstaller has no messages file, ' +
                    MsgFile + ', to set aside instead.');
                if not Kept then
                begin
                  if HoldUntilRestart(ExeFile) then
                  begin
                    UninstallerProgramHeld := True;
                    Log('Until the next restart the uninstaller there, ' + ExeFile + ', is not there to start, so it removes ' +
                      'nothing.');
                  end
                  else
                  begin
                    Result := False;
                    if HasMsg then
                      Log('None of ' + Path + ', ' + MsgFile + ' and ' + ExeFile + ' could be set aside until the next restart, ' +
                        'so that uninstaller can start before then.')
                    else
                      Log('Neither ' + Path + ' nor ' + ExeFile + ' could be set aside until the next restart, so that ' +
                        'uninstaller can start before then.');
                  end;
                end;
              end;
            end;
            """), Whole("HoldOldUninstaller"));
        Assert.Equal(OneLine("""
            function HoldUntilRestart(const Path: String): Boolean;
            var
              Aside: String;
            begin
              Result := True;
              if not FileExists(Path) then
                Exit;
              Aside := Path + '.' + RunStamp + '.old';
              if not RenameFile(Path, Aside) then
              begin
                Result := False;
                Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so it could not be set aside until the next restart.');
                Exit;
              end;
              try
                RestartReplace(Aside, Path);
                Log('Set aside ' + Path + ' until the next restart, when Windows puts it back.');
              except
                Result := False;
                Log('Windows could not be given ' + Aside + ' to put back at its next restart (' + GetExceptionMessage + '), so ' +
                  'it goes back now.');
                if not RenameFile(Aside, Path) then
                  Log('Could not put back ' + Path + ' from ' + Aside + '.');
              end;
            end;
            """), Whole("HoldUntilRestart"));
        Assert.Equal(OneLine("""
            function MovesAtRestart(const Path: String): Boolean;
            var
              Froms, Tos: TArrayOfString;
              I: Integer;
            begin
              Result := False;
              ReadRestartWork(Froms, Tos);
              for I := 0 to GetArrayLength(Froms) - 1 do
                if (Froms[I] = Lowercase(Path)) and (Tos[I] <> '') then
                  Result := True;
            end;
            """), Whole("MovesAtRestart"));
        Assert.Equal(OneLine("""
            function NameGoesBack(const App, Name: String): Boolean;
            var
              Find: TFindRec;
            begin
              Result := False;
              if FindFirst(App + '\' + Name + '.*.old', Find) then
              try
                repeat
                  if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(App + '\' + Find.Name) then
                    Result := True;
                until Result or not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("NameGoesBack"));
        Assert.Equal(OneLine("""
            function DeletedAtRestart(const Path: String): Boolean;
            var
              Froms, Tos: TArrayOfString;
              I: Integer;
            begin
              Result := False;
              ReadRestartWork(Froms, Tos);
              for I := 0 to GetArrayLength(Froms) - 1 do
                if (Froms[I] = Lowercase(Path)) and (Tos[I] = '') then
                  Result := True;
            end;
            """), Whole("DeletedAtRestart"));
        Assert.Equal(OneLine("""
            function UntilRestart(const App: String): String;
            var
              Starter: String;
            begin
              if DeletedAtRestart(App) then
                Result := 'PoolWatch is not installed: an earlier uninstall removed it, and Windows deletes what is left ' +
                  'of it at that restart'
              else
              begin
                Starter := LauncherPath(App);
                if (Starter = '') and not HasLauncher(App) and FileExists(LauncherFile(App)) and
                   not DeletedAtRestart(LauncherFile(App)) and not LeftNewHas(LauncherFile(App)) then
                  Starter := LauncherFile(App);
                if Starter = '' then
                  Result := 'PoolWatch does not start: no file that starts it is in place'
                else if DeletedAtRestart(Starter) then
                  Result := 'what starts PoolWatch starts a launcher and a program that another program keeps from ' +
                    'being taken back, without the other files of their version: Windows deletes that launcher at that restart'
                else if LeftNewHas(Starter) then
                  Result := 'what starts PoolWatch starts a launcher and a program that another program keeps from ' +
                    'being taken back, without the other files of their version, and Windows would not take that launcher to ' +
                    'delete at that restart, so it stays after it'
                else
                begin
                  if (NameGoesBack(App, PoolWatchName) and not FileExists(App + '\' + PoolWatchName)) or
                     (NameGoesBack(App, SelfContainedName) and not FileExists(SecondLauncherPath(App))) then
                    Result := 'the version you had stays in place, but for one of the names of its launcher, which ' +
                      'Windows puts back at that restart: what names that file starts nothing until then'
                  else
                    Result := 'the version you had stays in place';
                  if TakenOut > 0 then
                    Result := Result + '; Setup set some of its files aside so that the restart does not replace them (listed ' +
                      'below), so it may not start until then';
                end;
              end;
            end;
            """), Whole("UntilRestart"));
        Assert.Equal(OneLine("""
            procedure DropNewFolders(const App: String; Later: Boolean);
            var
              Dirs: TArrayOfString;
              Dir: String;
              I, J, N, Deepest: Integer;
            begin
              for I := 0 to GetArrayLength(Shipped) - 1 do
              begin
                Dir := ExtractFileDir(Shipped[I]);
                while (Length(Dir) > Length(App) + 1) and (CompareText(Copy(Dir, 1, Length(App) + 1), App + '\') = 0) do
                begin
                  J := 0;
                  while (J < GetArrayLength(Dirs)) and (CompareText(Dirs[J], Dir) <> 0) do
                    J := J + 1;
                  if J = GetArrayLength(Dirs) then
                  begin
                    N := GetArrayLength(Dirs);
                    SetArrayLength(Dirs, N + 1);
                    Dirs[N] := Dir;
                  end;
                  Dir := ExtractFileDir(Dir);
                end;
              end;
              for I := 0 to GetArrayLength(Dirs) - 1 do
              begin
                Deepest := 0;
                for J := 1 to GetArrayLength(Dirs) - 1 do
                  if Length(Dirs[J]) > Length(Dirs[Deepest]) then
                    Deepest := J;
                Dir := Dirs[Deepest];
                Dirs[Deepest] := '';
                if DirExists(Dir) then
                begin
                  if RemoveDir(Dir) then
                    Log('Deleted the folder ' + Dir + ', which only new files were in.')
                  else if Later then
                    try
                      RestartReplace(Dir, '');
                      Log('Windows deletes the folder ' + Dir + ' at its next restart, if it is empty then.');
                    except
                      Log('Windows could not be given the folder ' + Dir + ' to delete at its next restart: ' + GetExceptionMessage);
                    end;
                end;
              end;
            end;
            """), Whole("DropNewFolders"));
        Assert.Equal(OneLine("""
            procedure KeepLauncherOut(const App: String);
            var
              Froms, Tos: TArrayOfString;
              I, J: Integer;
              Path, Held: String;
            begin
              ReadRestartWork(Froms, Tos);
              for I := 1 to 2 do
              begin
                if I = 1 then
                  Path := App + '\' + PoolWatchName
                else
                  Path := SecondLauncherPath(App);
                Held := '';
                for J := 0 to GetArrayLength(Froms) - 1 do
                  if Tos[J] = Lowercase(Path) then
                    if FileExists(Froms[J]) then
                      if not RenameFile(Froms[J], Froms[J] + '.' + RunStamp + '.out.old') then
                        Held := Froms[J];
                if Held <> '' then
                  SetAsideAtRestart(Path, Held);
                if FileExists(Path) then
                  if not RenameFile(Path, Path + '.' + RunStamp + '.out.old') then
                    if Held = '' then
                    begin
                      Held := Path;
                      SetAsideAtRestart(Path, Held);
                    end;
                if Held = '' then
                  Log('Nothing is at ' + Path + ' after the next restart, so nothing starts PoolWatch from old and new files ' +
                    'mixed.');
              end;
            end;
            """), Whole("KeepLauncherOut"));
        Assert.Equal(OneLine("""
            procedure DropOldLayoutAside(const Path: String);
            var
              Find: TFindRec;
              Aside: String;
            begin
              if FindFirst(Path + '.*.old', Find) then
              try
                repeat
                  Aside := ExtractFileDir(Path) + '\' + Find.Name;
                  if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(Aside) then
                  begin
                    if DeleteFile(Aside) then
                      Log('Deleted ' + Aside + ', which was to go back at the next restart as ' + Path + ', a file of an earlier ' +
                        'layout of the install folder.')
                    else
                      try
                        RestartReplace(Path, '');
                        Log('Windows deletes ' + Path + ', a file of an earlier layout of the install folder, at its next restart, ' +
                          'after it goes back there from ' + Aside + HoldersOf(Aside) + '.');
                      except
                        Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
                      end;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("DropOldLayoutAside"));
        Assert.Equal(OneLine("""
            function ListedPath(const Item: String): String;
            begin
              Result := Item;
              if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then
                Result := Copy(Result, 3, Length(Result));
              if CharAt(Result, 1) = '!' then
                Result := Copy(Result, 2, Length(Result));
              if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then
                Result := Copy(Result, 3, Length(Result));
              if Copy(Result, 1, 4) = '\??\' then
                Result := Copy(Result, 5, Length(Result));
            end;
            """), Whole("ListedPath"));
        Assert.Equal(OneLine("""
            procedure ReadRestartWork(var Froms, Tos: TArrayOfString);
            var
              Work, Item, ValueName: String;
              V, P, Start, N: Integer;
              IsTo: Boolean;
            begin
              SetArrayLength(Froms, 0);
              SetArrayLength(Tos, 0);
              N := 0;
              for V := 1 to 2 do
              begin
                if V = 1 then
                  ValueName := 'PendingFileRenameOperations'
                else
                  ValueName := 'PendingFileRenameOperations2';
                if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then
                begin
                  Work := Lowercase(Work);
                  IsTo := False;
                  Start := 1;
                  for P := 1 to Length(Work) + 1 do
                    if CharAt(Work, P) = #0 then
                      if (P <= Length(Work)) or (Start <= Length(Work)) then
                      begin
                        Item := ListedPath(Copy(Work, Start, P - Start));
                        Start := P + 1;
                        if IsTo then
                          Tos[N - 1] := Item
                        else
                        begin
                          if N = GetArrayLength(Froms) then
                          begin
                            SetArrayLength(Froms, N + N div 2 + 16);
                            SetArrayLength(Tos, N + N div 2 + 16);
                          end;
                          N := N + 1;
                          Froms[N - 1] := Item;
                          Tos[N - 1] := '';
                        end;
                        IsTo := not IsTo;
                      end;
                end;
              end;
              SetArrayLength(Froms, N);
              SetArrayLength(Tos, N);
            end;
            """), Whole("ReadRestartWork"));
        Assert.Equal(OneLine("""
            function LeftNewHas(const Path: String): Boolean;
            begin
              Result := Pos(#0 + Lowercase(Path) + #0, #0 + LeftNew) > 0;
            end;
            """), Whole("LeftNewHas"));
        Assert.Equal(OneLine("""
            function FilledAtRestart(const Path: String): String;
            var
              Froms, Tos: TArrayOfString;
              Own, Name: String;
              I: Integer;
            begin
              Result := '';
              if FileExists(Path) then
                Result := Path;
              Name := Lowercase(Path);
              Own := Lowercase(StageDir) + '\';
              ReadRestartWork(Froms, Tos);
              for I := 0 to GetArrayLength(Froms) - 1 do
                if Copy(Froms[I], 1, Length(Own)) <> Own then
                begin
                  if Tos[I] = Name then
                  begin
                    if FileExists(Froms[I]) then
                      Result := Froms[I];
                  end
                  else if Froms[I] = Name then
                    Result := '';
                end;
            end;
            """), Whole("FilledAtRestart"));
        Assert.Equal(OneLine("""
            function LauncherLeftNew(const App: String): Boolean;
            begin
              Result := LeftNewHas(App + '\' + PoolWatchName) or LeftNewHas(SecondLauncherPath(App));
            end;
            """), Whole("LauncherLeftNew"));
        Assert.Equal(OneLine("""
            procedure SetAsideAtRestart(const Path, Held: String);
            begin
              try
                RestartReplace(Path, Path + '.' + RunStamp + '.later.old');
                Log('Another program has ' + Held + ' open' + HoldersOf(Held) + ', so Windows sets ' + Path + ' aside at its ' +
                  'next restart, after every move to it.');
              except
                LauncherKept := LauncherKept + 1;
                if CompareText(Held, Path) = 0 then
                  FinishFiles := FinishFiles + #13#10 + Path + ', which could not be set aside' + HoldersOf(Held)
                else
                  FinishFiles := FinishFiles + #13#10 + Path + ', where Windows moves ' + Held + ' at the restart: that file ' +
                    'could not be set aside' + HoldersOf(Held);
                Log('Another program has ' + Held + ' open' + HoldersOf(Held) + ', and Windows could not be given ' + Path +
                  ' to set aside at its next restart (' + GetExceptionMessage + '), so after the restart it may start PoolWatch ' +
                  'from old and new files mixed until a setup has replaced the files.');
              end;
            end;
            """), Whole("SetAsideAtRestart"));
        Assert.Equal(OneLine("""
            procedure RemoveOldLayout(const App: String; AtRestart: Boolean);
            var
              I: Integer;
              Path: String;
            begin
              I := 0;
              while OldLayoutFile(I) <> '' do
              begin
                Path := App + '\' + OldLayoutFile(I);
                if FileExists(Path) then
                begin
                  if not AtRestart and DeleteFile(Path) then
                    Log('Deleted ' + Path + ', which an earlier layout of the install folder left.')
                  else
                    try
                      RestartReplace(Path, '');
                      Log('Windows deletes ' + Path + ', which an earlier layout of the install folder left, at its next restart.');
                    except
                      Log('Windows could not be given ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
                    end;
                end
                else if AtRestart then
                  DropOldLayoutAside(Path);
                I := I + 1;
              end;
              Path := App + '\amd64';
              if DirExists(Path) then
              begin
                if not AtRestart and RemoveDir(Path) then
                  Log('Deleted the folder ' + Path + ', which an earlier layout of the install folder left.')
                else
                  try
                    RestartReplace(Path, '');
                    Log('Windows deletes the folder ' + Path + ' at its next restart, once it is empty.');
                  except
                    Log('Windows could not be given the folder ' + Path + ' to delete at its next restart: ' + GetExceptionMessage);
                  end;
              end;
            end;
            """), Whole("RemoveOldLayout"));
        Assert.InRange(Number("RunningWait"), 30, 300);
        Assert.InRange(Number("HandWait"), 1, 4);
        Assert.InRange(Number("BriefHold"), 1, 5);
        Assert.InRange(Number("MaxProcessesShown"), 1, 5);
        Assert.Equal(0x1000, Number16("ReadRights")); // PROCESS_QUERY_LIMITED_INFORMATION, and nothing more
        Assert.Equal(0x10, Number16("AttrFolder")); // FILE_ATTRIBUTE_DIRECTORY
        Assert.Equal(0x400, Number16("AttrLink")); // FILE_ATTRIBUTE_REPARSE_POINT
    }

    /// <summary>A file of the repository, by its path under the folder that holds PoolWatch.slnx.</summary>
    private static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return File.ReadAllText(Path.Combine([dir.FullName, .. parts]));
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheScriptLooksAtNoNamedObject()
    {
        // PoolWatch's mutex and event are in its private namespace (AdminNamespaceTests), which this script does not open:
        // it decides nothing by a named object, and has no mutex check or AppMutex of its own. Its one Global\ name is the
        // exit signal of a PoolWatch installed before the launcher (OldExitEvent), which it only ever sets (SetOldExit):
        // once when the setup asks that PoolWatch to exit (StopBeforeLauncher), then again each quarter of a second of the
        // wait that follows (StoppingOld), as that version's own stop did. What that does is left to the files and the
        // list of processes, as for any stop.
        string script = Script();
        Assert.Single(Regex.Matches(script, Regex.Escape("Global\\")));
        Assert.Contains("OldExitEvent = 'Global\\PoolWatch-Exit';", script);
        Assert.Single(Regex.Matches(script, @"OpenEvent\(EVENT_MODIFY_STATE"));
        Assert.Contains("Event := OpenEvent(EVENT_MODIFY_STATE, False, OldExitEvent);", Body("SetOldExit"));
        Assert.Single(Regex.Matches(script, @"\bSetEvent\(Event\)"));
        Assert.Equal(["SetOldExit(False);", "SetOldExit(True);"], Regex.Matches(Code(), @"\bSetOldExit\((True|False)\);").Select(m => m.Value)); // WaitForApp's, then StopBeforeLauncher's
        Assert.Single(Regex.Matches(script, @"StoppingOld := True;"));
        Assert.Contains("begin SetOldExit(True); StoppingOld := True; end;", Body("StopBeforeLauncher"));
        Assert.Contains("Sleep(250); if StoppingOld then SetOldExit(False); end;", Body("WaitForApp"));
        Assert.DoesNotContain("CheckForMutexes", script);
        Assert.DoesNotContain("WaitForSingleObject", script);
        Assert.DoesNotMatch(@"(?im)^\s*AppMutex\s*=", script);
    }

    /// <summary>The lines of one section of the script ([Files], [Run], ...), from its header line up to the next one.</summary>
    private static string Section(string name)
    {
        string script = Script();
        var header = Regex.Match(script, $@"(?m)^\[{name}\]\r?$");
        Assert.True(header.Success, name + " is not in the script");
        var next = Regex.Match(script[(header.Index + header.Length)..], @"(?m)^\[");
        return next.Success ? script.Substring(header.Index, header.Length + next.Index) : script[header.Index..];
    }

    [Fact]
    public void TheFilesDecideWhetherTheSetupReplacesThemNow()
    {
        // The setup replaces the files now only when Windows has no work in the install folder for its next restart and no
        // file of PoolWatch's program there is in use (a process that runs one or has it loaded, or another program that
        // has one open), asked of each file itself. The list of processes decides nothing: a process that runs one of
        // PoolWatch's programs has its file in use, and a token-leak test helper the wait has just ended can still be
        // listed for a moment. Otherwise the setup finishes at the next restart, naming each file in use with what has it
        // open (FinishFiles). Nothing in PrepareToInstall but /DIR stops it.
        string when = Whole("WhenToReplace");
        Assert.Contains("begin LookForRestartWork(App); Held := HeldFiles(App, True) > 0;", when);
        Assert.Contains("HeldUninstaller := Uninstaller; if Held then begin FinishFiles := HeldAll; " +
                        "FinishAtNextRestart('Some of PoolWatch''s files are in use, so they cannot be replaced now. Each is listed below with what has it open.'); end " +
                        "else if not FinishAtRestart then begin Log('No file in the install folder is in use: Setup replaces them now.');", when);
        Assert.DoesNotMatch(@"\bCount\b", when);
        Assert.DoesNotContain("Result :=", when);
        string prepare = Body("PrepareToInstall");
        Assert.EndsWith("WaitForApp(App, RunningWait); finally HideWaitPage; end; end; WhenToReplace(App); end;", Whole("PrepareToInstall"));
        Assert.Single(Regex.Matches(prepare, @"\bResult := '[^']")); // the one stop: /DIR
        Assert.Contains("Result := 'PoolWatch installs only to ' + ExpandConstant('{autopf}\\PoolWatch') + '. Run the setup without /DIR.';", prepare);
        // The wait ends once no file is in use, and then counts no process: whatever its last look at the list found
        // (a helper it ended, say) runs no file of PoolWatch's any more.
        string wait = Whole("WaitForApp");
        Assert.Contains("Held := HeldFiles(App, False) > 0; while Held and (Waited < Seconds) and ((Result <> 0) or (Waited < BriefHold)) do", wait);
        Assert.Contains("Held := HeldFiles(App, False) > 0; end; if not Held and (Result <> 0) then begin Log(", wait);
        Assert.Contains("'the list of processes found is not counted.'); Result := 0; end;", wait);
        // A file another program has open only for a moment (a virus scanner, the search indexer) gets BriefHold seconds
        // even when nothing of PoolWatch runs.
        Assert.InRange(Number("BriefHold"), 1, 5);
        // The probe: read and write access with no sharing, which Windows refuses while a process has the file mapped (an
        // exe it runs, a DLL it loaded: no write) or any other program has it open; it changes nothing.
        Assert.Contains("Probe := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive); Probe.Free; Result := True; except Result := False; end;", Body("CanReplace"));
        // An earlier setup's restart folder, once its restart has happened (no work pending), is tidied away.
        Assert.Contains("if DirExists(App + '\\' + StageFolder) then if not DelTree(App + '\\' + StageFolder, True, True, True) then", when);
    }

    /// <summary>
    /// The setup closes no program, another account's or a service included: a file another program keeps in use sends the
    /// update to the next restart instead. Inno Setup 6.7.3 closes the programs that have one of its files open only through
    /// its own Restart Manager session: it starts one only when CloseApplications is yes or force (unless
    /// /NOCLOSEAPPLICATIONS is given) or /CLOSEAPPLICATIONS is on its command line (Setup.MainFunc.pas, InitializeSetup); it
    /// registers with it only the files whose names match CloseApplicationsFilter, and those [Code] adds in
    /// RegisterExtraCloseApplicationsResources (Setup.MainFunc.pas, RegisterFile and RegisterResourcesWithRestartManager); it
    /// asks Restart Manager for their programs only when it registered a file (Setup.WizardForm.pas, QueryRestartManager),
    /// and closes them (ShutdownApplications in Setup.Install.HelperFunc.pas, through RmShutdown) only when it found one
    /// (Setup.Install.pas, PerformInstall). So with CloseApplications off, a filter that no file of the setup's matches and
    /// no RegisterExtraCloseApplicationsResources, it closes none even with /CLOSEAPPLICATIONS. The script's own Restart
    /// Manager calls (HoldersOf) only name what has a file open: RmGetList, never RmShutdown. CI's installer test holds the
    /// setup to it with a file held by another process.
    /// </summary>
    [Fact]
    public void TheSetupClosesNoProgram()
    {
        string setup = Section("Setup");
        Assert.Matches(@"(?m)^CloseApplications=no\r?$", setup);
        Assert.Matches(@"(?m)^RestartApplications=no\r?$", setup);
        Assert.Single(Regex.Matches(setup, @"(?im)^CloseApplications="));
        string filter = Regex.Match(setup, @"(?m)^CloseApplicationsFilter=(.*?)\r?$").Groups[1].Value;
        Assert.Equal("*.closes-no-program", filter); // one pattern (no comma), of a kind no file of the setup's has
        Assert.DoesNotMatch(@"(?im)^CloseApplicationsFilterExcludes", setup);
        Assert.DoesNotContain(".closes-no-program", Section("Files"), StringComparison.OrdinalIgnoreCase);
        string code = CodeSection();
        Assert.DoesNotContain("RegisterExtraCloseApplicationsResources", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RmShutdown", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RmShutdown", Script(), StringComparison.OrdinalIgnoreCase);
        // CI's check, on a setup run while another process has app\PoolWatch.exe in use: Inno Setup logs "Found <n> files to
        // register with RestartManager." whenever its session is started and asked (QueryRestartManager), and "Shutting
        // down applications using our files." before it closes any (ShutdownApplications); neither may be in the log, and
        // the process must still run (the test reads that through the handle it holds to it, never by its ID alone).
        string e2e = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "installer-e2e.ps1"));
        Assert.Contains("Assert-True (($setupLog -notmatch 'files to register with RestartManager|Shutting down applications using our files') -and " +
                        "-not $process.HasExited) \"${name}: the setup closed no program", e2e);
    }

    [Fact]
    public void TheNoteOnCloseApplicationsSaysTheSetupClosesNoProgramAndHowLongItWaits()
    {
        // The [Setup] note above CloseApplications says that the setup closes no program, and what the script does in its
        // place: how long WaitForApp waits (while a process with PoolWatch's name runs, or the list cannot be read, up to
        // RunningWait; otherwise up to BriefHold), and that a file still in use after WhenToReplace's own BriefHold, or
        // one that stays in use while SetAllAside sets the old files aside, sends the whole update to the restart. The
        // code each part says is held here beside it.
        var note = Regex.Match(Section("Setup"), @"(?m)((?:^;[^\r\n]*\r?\n)+)^CloseApplications=no\r?$");
        Assert.True(note.Success, "CloseApplications has a note above it");
        Assert.Equal("The setup closes no program. Inno Setup's own close of the programs that have a file of the setup open (through Restart " +
                     "Manager, without asking in a silent setup) is off, and its filter matches no file the setup has, so Inno Setup registers none and " +
                     "closes none even when /CLOSEAPPLICATIONS on its command line turns that close on. PrepareToInstall asks a running PoolWatch to " +
                     "exit, then waits, up to RunningWait seconds, until none of its files is in use, for as long as a process with PoolWatch's name runs " +
                     "from the install folder or the list of processes cannot be read, and up to BriefHold seconds when no such process runs " +
                     "(WaitForApp). When a file is still in use after that and BriefHold seconds more (WhenToReplace), or stays in use while the old files " +
                     "are set aside (SetAllAside), the whole update goes in place at the next restart.",
            OneLine(Regex.Replace(note.Groups[1].Value, @"(?m)^;\s?", "")));
        Assert.Contains("if Blocked <> '' then Why := Blocked + HoldersOf(Blocked) + ' came into use while Setup was setting the old files aside';", Whole("SetAllAside"));
        Assert.Contains("FinishAtNextRestart(Why);", Whole("SetAllAside"));
        Assert.Contains("WaitForApp(App, RunningWait); finally HideWaitPage; end; end; WhenToReplace(App);", Body("PrepareToInstall"));
        Assert.Contains("while Held and (Waited < Seconds) and ((Result <> 0) or (Waited < BriefHold)) do", Whole("WaitForApp"));
        Assert.Contains("if Listed then Result := RunningFromApp(Service, Exe, Launcher, Second, True, True) else Result := -1;", Whole("WaitForApp"));
        Assert.Contains("while Held and (Quarter < BriefHold * 4) do", Whole("WhenToReplace"));
        Assert.Contains("if Held then begin FinishFiles := HeldAll; FinishAtNextRestart(", Whole("WhenToReplace"));
    }

    [Fact]
    public void AnInPlaceUpgradeSetsEveryOldFileAsideFirstAndKeepsThemUntilItIsDone()
    {
        // Before [Files] writes any file (ssInstall), every old file of PoolWatch's program is renamed aside, each only once
        // it is free, and each is looked at again under its new name: a file that comes into use part way through can
        // then only stop the setup before anything new is in place, and every file goes back and the setup finishes at the
        // next restart instead, naming every file in use. So PoolWatch never runs from old and new files mixed.
        string set = Whole("SetAllAside");
        Assert.Contains("if FinishAtRestart then Exit; SelfContainedNameThere; Blocked := ''; Why := ''; try SetAsideIn(App, '', Blocked);", set);
        Assert.Contains("while (Blocked = '') and (I < GetArrayLength(AsideNames)) do begin if not FreeSoon(AsideNames[I]) then Blocked := AsideNames[I];", set);
        Assert.Contains("else begin PutAllBack(False); Why := Why + ', so none of them is replaced now.'; if HeldFiles(App, True) > 0 then begin " +
                        "FinishFiles := HeldAll; Why := Why + ' Each file in use now is listed below with what has it open.'; end; FinishAtNextRestart(Why); end; end;", set);
        string aside = Whole("SetAsideIn");
        Assert.Contains("Aside := Path + '.' + RunStamp + '.old'; if SetAsideSoon(Path, Aside) then begin", aside);
        Assert.Contains("else Blocked := Path;", aside);
        // Not the restart folder, an uninstaller's folder or the uninstaller's own files, which Inno Setup writes itself.
        Assert.Contains("((Rel <> '') or ((CompareText(Name, StageFolder) <> 0) and (CompareText(Name, UninstallFolder) <> 0) and " +
                        "(CompareText(Copy(Name, 1, 5), 'unins') <> 0)))", aside);
        // The folder is listed whole first, and its files renamed after: a name set aside sorts right after its own and
        // would otherwise come back from the same listing to be renamed again. Nothing is renamed while it is listed.
        int list = aside.IndexOf("until not FindNext(Find); finally FindClose(Find); end;", StringComparison.Ordinal);
        int rename = aside.IndexOf("SetAsideSoon(", StringComparison.Ordinal);
        Assert.True(list > 0 && rename > list, aside);
        Assert.Single(Regex.Matches(aside, @"\bSetAsideSoon\("));
        Assert.DoesNotMatch(@"\bRenameFile\(", aside); // only through SetAsideSoon, which renames a file only once it is free
        Assert.Contains("I := 0; while (Blocked = '') and (I < GetArrayLength(Found)) do begin if Copy(Found[I], 1, 1) = '\\' then begin " +
                        "Name := Copy(Found[I], 2, Length(Found[I]) - 1); SetAsideIn(Dir + '\\' + Name, Rel + Name + '\\', Blocked); end", aside);
        Assert.Contains("N := GetArrayLength(Found); SetArrayLength(Found, N + 1); if (Find.Attributes and AttrFolder) <> 0 then Found[N] := '\\' + Name else Found[N] := Name;", aside);
        Assert.EndsWith("I := I + 1; end; end;", aside);
        string step = Whole("CurStepChanged");
        Assert.Contains("if CurStep = ssInstall then begin InstallBegun := True; OldUninstaller := LauncherPath(App) = ''; SaveLinkBefore; SaveEntryBefore; SetAllAside(App); SaveUninstallerBefore(App); end", step);
        // The old files go only once every new one is in place (ssPostInstall): each whose new file is there is deleted.
        Assert.Contains("else if CurStep = ssPostInstall then begin InstallDone := True; if FinishAtRestart then", step);
        Assert.Contains("else begin DropAside; RemoveOldLayout(App, False);", step);
        // The launcher's second name is read before that file is renamed, and kept for [Files].
        Assert.Contains("if not SecondNameLooked then begin SecondNameLooked := True; SecondNameWas := FileExists(SecondLauncherPath(ExpandConstant('{app}'))); end; " +
                        "Result := SecondNameWas;", Whole("SelfContainedNameThere"));
    }

    [Fact]
    public void AnOldFileGoesBackAfterTheUpgradeOnlyWhenThisVersionDoesNotHaveIt()
    {
        // Each [Files] entry of the program notes the file it is about to write (Shipping), whether or not it is written:
        // so after the upgrade an empty place whose file this version has (one Inno Setup was told to skip) keeps its old
        // file set aside, and only a file this version does not have is put back. No old file goes back beside new ones.
        var entries = Regex.Matches(Section("Files"), @"(?m)^Source: ""\{#PublishDir\}.*$").Select(m => m.Value.TrimEnd('\r')).ToList();
        Assert.Equal(9, entries.Count); // the build's 8, and the notices' icon (poolwatch.png, TrayAppNoticeTests)
        Assert.All(entries, e => Assert.EndsWith("; BeforeInstall: Shipping", e));
        Assert.DoesNotContain("AfterInstall", Script());
        Assert.Contains("N := GetArrayLength(Shipped); SetArrayLength(Shipped, N + 1); Shipped[N] := ExpandConstant(CurrentFileName);", Whole("Shipping"));
        Assert.Contains("for I := 0 to GetArrayLength(Shipped) - 1 do if CompareText(Shipped[I], Path) = 0 then Result := True;", Whole("Ships"));
        Assert.Contains("if FileExists(AsideTargets[I]) or (CompareText(ExtractFileExt(AsideTargets[I]), '.old') = 0) or " +
                        "(CompareText(ExtractFileName(AsideTargets[I]), KeepName) = 0) then DeleteNowOrAtRestart(AsideNames[I]) " +
                        "else if Ships(AsideTargets[I]) then Log('Setup did not write ' + AsideTargets[I] + ', which this version has, so the old one stays set aside as ' + " +
                        "AsideNames[I] + ' and is not put back.') else PutBack(AsideTargets[I], AsideNames[I]);", Whole("DropAside"));
    }

    [Fact]
    public void ASetupThatDoesNotFinishPutsEveryOldFileBack()
    {
        // Inno Setup's rollback (a cancel, or a step that fails after the files) deletes every file this run wrote whose
        // place was empty before, which every renamed-aside file's place was; so when the setup did not get to its end,
        // every old file goes back, every file this version has that is still there (a new one the rollback could not
        // delete, also one with no old file in its place) is taken back first, and the Start menu entry, which the
        // rollback deletes too, is put back from the copy made before the files were set aside.
        string deinit = Whole("DeinitializeSetup");
        Assert.StartsWith("procedure DeinitializeSetup; var I: Integer; Later, Held: Boolean; App, Link, Text: String; begin if InstallDone or not InstallBegun then Exit; " +
                          "App := ExpandConstant('{app}'); Later := False; " +
                          "if not FinishAtRestart then for I := 0 to GetArrayLength(Shipped) - 1 do if not TakeBack(Shipped[I]) then Later := True; " +
                          "PutAllBack(Later); if not FinishAtRestart then DropNewFolders(App, Later); PutUninstallerBack; PutEntryBack;", deinit);
        Assert.Contains("if (LinkBefore <> '') and not FileExists(Link) then if FileCopy(LinkBefore, Link, True) then", deinit);
        string back = Whole("PutBack");
        Assert.Contains("Result := True; if FileExists(Target) or not FileExists(Aside) then Exit; if RenameFile(Aside, Target) then begin " +
                        "Log('Put back ' + Target + '.'); Exit; end;", back);
        Assert.Contains("if FileCopy(Aside, Target, True) then How := ' as a copy: ' else if CreateHardLinkW(Target, Aside, 0) then " +
                        "How := ' under a second name of the same file: ' else How := '';", back);
        Assert.Contains("if How <> '' then begin Log('Put back ' + Target + How + Aside + ' is in use' + HoldersOf(Aside) + '.'); try RestartReplace(Aside, '');", back);
        Assert.Contains("else begin Result := False; try RestartReplace(Aside, Target);", back);
        Assert.Contains("if FileCopy(Link, ExpandConstant('{tmp}\\PoolWatch.lnk'), False) then LinkBefore := ExpandConstant('{tmp}\\PoolWatch.lnk')",
            Whole("SaveLinkBefore"));
        // InstallDone is set only at ssPostInstall, the end of a setup that got there.
        Assert.Single(Regex.Matches(Code(), @"\bInstallDone := True;"));
    }

    [Fact]
    public void ASetupStoppedBeforeItChangesPoolWatchsFilesPutsNothingBackAndNeverAsksForTheInstallFolder()
    {
        // DeinitializeSetup runs however Setup ends, also after a cancel on the Welcome page. Inno Setup knows the install
        // folder only once its wizard has passed the folder page (WizardDirValue, set as it passes even a hidden page), and
        // expanding {app} before then raises an internal error, which Setup reports in an error box ("DeinitializeSetup
        // raised an exception"). None of PoolWatch's files or entries is changed before ssInstall (every file set aside, the
        // copies of the entries, the uninstaller's files: all from there on); before it the setup may only have asked
        // PoolWatch to exit, ended a token-leak test helper and deleted what an earlier setup left in its own folder for a
        // restart that has happened since (WhenToReplace), none of which a step puts back (it closes no program:
        // TheSetupClosesNoProgram). So DeinitializeSetup does nothing until the setup got there, and expands {app} only
        // after that test.
        string deinit = Whole("DeinitializeSetup");
        Assert.StartsWith("procedure DeinitializeSetup; var I: Integer; Later, Held: Boolean; App, Link, Text: String; begin " +
                          "if InstallDone or not InstallBegun then Exit; App := ExpandConstant('{app}');", deinit);
        // InstallBegun is set in one place: first thing at ssInstall, before anything there can change a file or an entry
        // (the steps after it there are held in order by AFailedSetupPutsBackTheUninstallerItReplacedAndTheAppsAndFeaturesEntry).
        Assert.Single(Regex.Matches(Code(), @"\bInstallBegun := True;"));
        Assert.Contains("App := ExpandConstant('{app}'); if CurStep = ssInstall then begin InstallBegun := True; OldUninstaller := ", Whole("CurStepChanged"));
    }

    [Fact]
    public void UntilEveryOldFileIsBackNoOldFileIsBesideANewOne()
    {
        // A new file another program has open without letting it be renamed or deleted (FILE_SHARE_DELETE) stays where the
        // rollback could not delete it until the next restart, when Windows deletes it; an old file that cannot be put
        // back now even as a copy or under a second name (CreateHardLinkW) cannot go back before then either. When a new
        // file stays, every old file goes back only then, so the install folder holds only new files until then; when
        // an old file cannot go back now, it and every one after it go back then, the launcher's two names always among
        // them and last, so no launcher is there to start PoolWatch from a version that is not whole (PoolWatch itself
        // started any other way hands its start to the launcher). Either way no old file is ever beside a new one until
        // the restart. When Windows will not take such a new file to delete at the restart either (MoveFileEx fails when
        // it cannot write its list) and it stays at its own name, as another program kept it from being renamed (LeftNew),
        // it stays after the restart, so the launcher's names are not given to Windows to move back at all: they stay set
        // aside, and start nothing from the old files that go back beside it. When that new file is at one of the
        // launcher's names (LauncherLeftNew, KeepAll), no old file is given to Windows: that new launcher, and the new
        // program when it stays too, start nothing from old files after the restart either.
        string all = Whole("PutAllBack");
        Assert.Contains("Late := Later and (GetArrayLength(AsideTargets) > 0); KeepAll := LauncherLeftNew(ExpandConstant('{app}')); Back := 0; for Pass := 1 to 2 do " +
                        "for I := 0 to GetArrayLength(AsideTargets) - 1 do if StartsPoolWatch(AsideTargets[I]) = (Pass = 2) then begin " +
                        "if Late and (LeftNew <> '') and ((Pass = 2) or KeepAll) then Log('A new file stays after the next restart, so ' + AsideTargets[I] + " +
                        "' stays set aside as ' + AsideNames[I] + ' then too, so that nothing starts PoolWatch from old and new files mixed.') " +
                        "else if Late then MoveBackAtRestart(AsideTargets[I], AsideNames[I]) " +
                        "else if not PutBack(AsideTargets[I], AsideNames[I]) then Late := True else if Pass = 2 then Back := Back + 1; end;", all);
        Assert.Equal("function LauncherLeftNew(const App: String): Boolean; begin Result := LeftNewHas(App + '\\' + PoolWatchName) or " +
                     "LeftNewHas(SecondLauncherPath(App)); end;", Whole("LauncherLeftNew"));
        // LeftNew holds each new file TakeBack left at its own name with its delete refused, and only that: one TakeBack
        // renamed but could not delete stays under its new name, which starts nothing, and is not in it.
        Assert.Single(Regex.Matches(Code(), @"\bLeftNew := "));
        Assert.Contains("except Refused := Refused + 1; LeftNew := LeftNew + Lowercase(Path) + #0; Log('Windows could not be given ' + Path + ' to delete", Whole("TakeBack"));
        Assert.Contains("try RestartReplace(Moved, ''); Log('Windows deletes ' + Moved + ' at its next restart.'); except Refused := Refused + 1; Log(", Whole("TakeBack"));
        // Any file left for the restart is noted (the uninstaller then starts no cleanup until then, and the setup says
        // so); what starts PoolWatch until then is read from the folder afterwards (UntilRestart), not from this run.
        Assert.Contains("if Late then begin OldAtRestart := True; if KeepAll then Log(", all);
        Assert.Contains("else if LeftNew <> '' then Log(", all);
        Assert.Contains("else if Back = 0 then Log(", all);
        // The box says which: every file of the version that was there set aside when a new launcher stays.
        Assert.Contains("if LauncherLeftNew(App) then Text := Text + ', and a new file that starts PoolWatch stays", Whole("DeinitializeSetup"));
        Assert.Single(Regex.Matches(Code(), @"\bOldAtRestart := True;"));
        Assert.DoesNotContain("LauncherAtRestart", Code());
        Assert.EndsWith("SetArrayLength(AsideTargets, 0); SetArrayLength(AsideNames, 0); end;", all);
        Assert.Contains("Result := (CompareText(Path, ExpandConstant('{app}') + '\\' + PoolWatchName) = 0) or " +
                        "(CompareText(Path, SecondLauncherPath(ExpandConstant('{app}'))) = 0);", Whole("StartsPoolWatch"));
        Assert.Equal(Engine.LaunchGate.LauncherFileNames, [Constant("PoolWatchName"), Constant("SelfContainedName")]);
        Assert.Contains("if not FileExists(Aside) then Exit; try RestartReplace(Aside, Target);", Whole("MoveBackAtRestart"));
        // PutBack says when a file is not back now: only its last branch, which leaves it to the restart.
        string back = Whole("PutBack");
        Assert.Single(Regex.Matches(back, @"Result := False;"));
        Assert.True(back.IndexOf("Result := False;", StringComparison.Ordinal) > back.IndexOf("else if CreateHardLinkW(Target, Aside, 0) then", StringComparison.Ordinal), back);
        // The two callers: the setup that stops setting aside (nothing new written yet), and the one that did not finish,
        // which sends every old file to the restart when any new file could not be taken back (TakeBack).
        Assert.Contains("PutAllBack(False);", Whole("SetAllAside"));
        Assert.Contains("if not TakeBack(Shipped[I]) then Later := True; PutAllBack(Later);", Whole("DeinitializeSetup"));
        Assert.Equal(2, Regex.Matches(Code(), @"\bPutAllBack\((False|Later)\);").Count);
    }

    [Fact]
    public void ANewFileTheSetupCouldNotTakeBackSendsEveryOldFileToTheRestart()
    {
        // After a setup that did not finish, every file this version has (Shipped) that is still in the install folder is
        // a new one Inno Setup's rollback could not delete: one in an old file's place, and one with no old file there
        // (app\PoolWatch.exe and app\amd64\ on an upgrade from 1.0.0-beta.1). Each is taken back alike: renamed out of the
        // way and deleted. One that cannot be renamed (another program has it open without FILE_SHARE_DELETE), and one
        // renamed but not deleted (a program runs it or has it loaded), is not taken back: Windows deletes it at the next
        // restart, before the old files go back, and every old file goes back only then. So no old file is ever beside a
        // new one, nor beside a new PoolWatch that runs.
        string take = Whole("TakeBack");
        Assert.Contains("Result := True; if not FileExists(Path) then Exit; Moved := Path + '.' + RunStamp + '.new'; if not RenameFile(Path, Moved) then " +
                        "begin Result := False; Log('Another program has ' + Path + ' open' + HoldersOf(Path) + ', so it stays until the next restart, and no old ' + " +
                        "'file goes back before then.'); try RestartReplace(Path, '');", take);
        Assert.Contains("else if DeleteFile(Moved) then Log('Took back ' + Path + '.') else begin Result := False;", take);
        Assert.Contains("try RestartReplace(Moved, ''); Log('Windows deletes ' + Moved + ' at its next restart.');", take);
        // Its log says what is sure: no old file goes back before the restart (each then, or none when a new file stays).
        Assert.Contains("Log('Could not delete ' + Moved + ' now' + HoldersOf(Moved) + ', so no old file goes back before the next ' + 'restart.');", take);
        Assert.DoesNotContain("puts every old file back", take);
        Assert.Equal(3, Regex.Matches(take, @"\bResult := ").Count); // True first, then False in each branch that leaves a file
        // Every file [Files] was about to write, not only the places of old files set aside; not when the files went to
        // the setup's own folder (FinishAtRestart: nothing new is in the install folder, and nothing was set aside).
        string deinit = Whole("DeinitializeSetup");
        Assert.Contains("if not FinishAtRestart then for I := 0 to GetArrayLength(Shipped) - 1 do if not TakeBack(Shipped[I]) then Later := True;", deinit);
        Assert.DoesNotContain("AsideTargets", deinit);
        // Each new file's delete at the restart comes before the old files' moves back over the same places.
        Assert.True(deinit.IndexOf("TakeBack(", StringComparison.Ordinal) < deinit.IndexOf("PutAllBack(Later);", StringComparison.Ordinal), deinit);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr reserved);

    [Fact]
    public void AnOldFileAnotherProgramSharesWithNoOneGoesBackUnderASecondName()
    {
        // PutBack, for an old file another program opened under the name it was set aside under: the rename back fails
        // without FILE_SHARE_DELETE, a copy fails without FILE_SHARE_READ, and a second name of the same file
        // (CreateHardLinkW) is still made, as Windows checks the share modes of a file's open handles only for an open that
        // reads, writes, runs or deletes it. This holds that rule of Windows', with a FileStream that shares the file with
        // no one; then, as Windows does at the restart, the name it was set aside under goes and the file stays.
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-link-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string aside = Path.Combine(dir, "LICENSE.1.old"), target = Path.Combine(dir, "LICENSE");
            File.WriteAllText(aside, "the file that was there");
            using (new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => File.Move(aside, target));
                Assert.Throws<IOException>(() => File.Copy(aside, target));
                Assert.False(File.Exists(target));
                Assert.True(CreateHardLinkW(target, aside, IntPtr.Zero), $"error {Marshal.GetLastWin32Error()}");
            }
            File.Delete(aside);
            Assert.Equal("the file that was there", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        // The script's order: renamed back, else copied, else given a second name; only then left for the restart.
        string back = Whole("PutBack");
        int rename = back.IndexOf("if RenameFile(Aside, Target) then", StringComparison.Ordinal);
        int copy = back.IndexOf("if FileCopy(Aside, Target, True) then", StringComparison.Ordinal);
        int link = back.IndexOf("else if CreateHardLinkW(Target, Aside, 0) then", StringComparison.Ordinal);
        int late = back.IndexOf("RestartReplace(Aside, Target);", StringComparison.Ordinal);
        Assert.True(rename > 0 && copy > rename && link > copy && late > link, back);
        Assert.Equal("function CreateHardLinkW(FileName, ExistingFileName: String; Reserved: THandle): BOOL; external 'CreateHardLinkW@kernel32.dll stdcall';",
            Whole("CreateHardLinkW"));
    }

    [Fact]
    public void AFailedUpgradeFromAVersionBeforeTheLauncherKeepsItsUninstallerFromRemovingAnythingUntilTheRestart()
    {
        // 1.0.0-beta.1's uninstaller (its record, unins000.dat, is still that version's after a setup that did not finish:
        // Inno Setup saves its record last) starts the install folder's PoolWatch.exe to put back PoolWatch's changes, and
        // goes on without that when it does not run. After a setup that left old files to go back at the restart, it would
        // remove the files and the Apps and Features entry and leave the changes and the logon task, and the restart would
        // then bring that version's files back with no entry. So when the install folder had no launcher of its program's
        // version as the setup began, each uninstaller record there is set aside until that restart, when Windows puts it
        // back, and the setup says the uninstaller removes nothing until then. When another program keeps the record from
        // being renamed, the uninstaller's messages file (unins000.msg) is set aside instead: a signed uninstaller, as
        // 1.0.0-beta.1's released setup made it, reads its messages from that file before it looks for its record, and
        // stops without them (Inno Setup 6.7.3, RunUninstaller and LoadSetupMessages), so it removes nothing either. When
        // neither can be set aside, its program, unins000.exe, is set aside in their place, so that nothing can start that
        // uninstaller until the restart; the setup then says so, and to answer No if Windows offers to remove PoolWatch
        // from its list of apps before then. When none of the three can be set aside (another program has each open
        // without FILE_SHARE_DELETE, which no program can rename then, or Windows will not take them for the restart),
        // that uninstaller can run before the restart: the setup then says not to start it, and what it would do, whether
        // or not Windows refused other work for the restart. This version's own uninstaller starts no cleanup until then
        // of itself (RunCleanup).
        Assert.Contains("if CurStep = ssInstall then begin InstallBegun := True; OldUninstaller := LauncherPath(App) = ''; SaveLinkBefore;", Whole("CurStepChanged"));
        Assert.Single(Regex.Matches(Code(), @"\bOldUninstaller := "));
        string deinit = Whole("DeinitializeSetup");
        Assert.Contains("if OldAtRestart then begin Held := True; if OldUninstaller then Held := HoldOldUninstaller(App);", deinit);
        Assert.True(deinit.IndexOf("PutAllBack(Later);", StringComparison.Ordinal) < deinit.IndexOf("if OldAtRestart then", StringComparison.Ordinal), deinit);
        // The uninstaller's words: what an uninstaller that could not be held would do, that it is not there to start
        // (its program set aside, with what to answer Windows), or that it removes nothing (only when that is sure: one
        // made before the launcher that was held, or this version's while Windows took every move back); then, apart from
        // them, what to do after the restart.
        Assert.Contains("if not Held then Text := Text + ' Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not ' + " +
                        "'be kept from running until then (Setup''s log says why), and it would not wait for the restart. It would ' + " +
                        "'remove PoolWatch''s Apps and Features entry and keep its changes and its sign-in task, and the files Windows ' + " +
                        "'puts back at the restart would then have no entry.' else if UninstallerProgramHeld then Text := Text + ' Until that restart " +
                        "PoolWatch''s uninstaller is not there to start, so it removes nothing: if ' + 'Windows offers to remove PoolWatch from its list of apps " +
                        "before then, answer No.' else if OldUninstaller or (Refused = 0) then Text := Text + ' Until that restart " +
                        "PoolWatch''s uninstaller removes nothing.'; if Refused > 0 then Text := Text + ' Run the setup again before you uninstall PoolWatch.' " +
                        "else Text := Text + ' Uninstall PoolWatch, if you want to, after the restart.';", deinit);
        Assert.Contains("Log(Text); if not WizardSilent then MsgBox(Text, mbInformation, MB_OK); end; end;", deinit);
        Assert.Single(Regex.Matches(Code(), @"\bUninstallerProgramHeld := True;"));
        string hold = Whole("HoldOldUninstaller");
        Assert.Contains("Result := True; if FindFirst(App + '\\unins???.dat', Find) then", hold);
        // The record first, then its messages file when it is there, then the uninstaller's program.
        Assert.Contains("Path := App + '\\' + Found[I]; MsgFile := ChangeFileExt(Path, '.msg'); ExeFile := ChangeFileExt(Path, '.exe'); HasMsg := FileExists(MsgFile); " +
                        "Kept := HoldUntilRestart(Path); if Kept then Log('Until the next restart the uninstaller there finds no record, ' + Path + ', and removes nothing.') " +
                        "else if HasMsg then begin Kept := HoldUntilRestart(MsgFile); if Kept then Log('Until the next restart the uninstaller there finds no messages, ' + " +
                        "MsgFile + ', and removes nothing.'); end else Log(", hold);
        Assert.Contains("if not Kept then begin if HoldUntilRestart(ExeFile) then begin UninstallerProgramHeld := True; Log('Until the next restart the uninstaller " +
                        "there, ' + ExeFile + ', is not there to start, so it removes ' + 'nothing.'); end else begin Result := False; if HasMsg then Log('None of ' + " +
                        "Path + ', ' + MsgFile + ' and ' + ExeFile + ' could be set aside until the next restart, ' + 'so that uninstaller can start before then.') " +
                        "else Log('Neither ' + Path + ' nor ' + ExeFile + ' could be set aside until the next restart, so that ' + 'uninstaller can start before then.'); " +
                        "end; end;", hold);
        Assert.Equal(3, Regex.Matches(hold, @"\bHoldUntilRestart\(").Count);
        Assert.True(hold.IndexOf("HoldUntilRestart(Path)", StringComparison.Ordinal) < hold.IndexOf("HoldUntilRestart(MsgFile)", StringComparison.Ordinal) &&
                    hold.IndexOf("HoldUntilRestart(MsgFile)", StringComparison.Ordinal) < hold.IndexOf("HoldUntilRestart(ExeFile)", StringComparison.Ordinal), hold);
        Assert.Single(Regex.Matches(hold, @"Result := False;"));
        string until = Whole("HoldUntilRestart");
        Assert.Contains("Result := True; if not FileExists(Path) then Exit; Aside := Path + '.' + RunStamp + '.old'; if not RenameFile(Path, Aside) then " +
                        "begin Result := False;", until);
        Assert.Contains("try RestartReplace(Aside, Path); Log('Set aside ' + Path + ' until the next restart, when Windows puts it back.'); except Result := False;", until);
        Assert.Contains("if not RenameFile(Aside, Path) then Log('Could not put back ' + Path + ' from ' + Aside + '.');", until);
        // The setup never sets the uninstaller's own files aside with the program's, so the record is still the old one.
        Assert.Contains("(CompareText(Copy(Name, 1, 5), 'unins') <> 0)", Whole("SetAsideIn"));
    }

    /// <summary>A copy of the script's MovesAtRestart, which a test holds to Windows' list as Windows writes it.</summary>
    private static bool ScriptMovesAtRestart(string data, string path) =>
        ScriptRestartWork(data).Any(pair => pair.From == path.ToLowerInvariant() && pair.To != "");

    /// <summary>A copy of the script's GoesAtRestart, which a test holds to Windows' list as Windows writes it.</summary>
    private static bool ScriptGoesAtRestart(string data, string path) =>
        ScriptRestartWork(data).Any(pair => pair.From == path.ToLowerInvariant());

    [Fact]
    public void TheUninstallerRemovesNothingWhileALauncherNameGoesBackAtTheRestart()
    {
        // When a setup could not put back one of the launcher's names now, Windows moves its old file back at the next
        // restart, from <name>.<run>.old. An uninstall before then would remove the folder that move goes into, and the
        // restart would bring that file back alone, so the uninstaller starts no cleanup and removes nothing (NotInPlace,
        // whose words say to restart Windows first while Windows has work in the install folder). An old launcher set aside
        // by an update that finished is only deleted at the restart: that does not count.
        string run = Whole("RunCleanup");
        Assert.Contains("else if NameGoesBack(ExpandConstant('{app}'), PoolWatchName) or NameGoesBack(ExpandConstant('{app}'), SelfContainedName) then begin " +
                        "Log('One of the launcher''s names in the install folder goes back only at the next restart, so PoolWatch''s cleanup ' + " +
                        "'was not started.'); Result := NotInPlace; end", run);
        Assert.True(run.IndexOf("NameGoesBack(", StringComparison.Ordinal) < run.IndexOf("Exec(Launcher", StringComparison.Ordinal), run);
        string name = Whole("NameGoesBack");
        Assert.Contains("if FindFirst(App + '\\' + Name + '.*.old', Find) then", name);
        Assert.Contains("if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(App + '\\' + Find.Name) then Result := True;", name);
        Assert.Contains("Aside := Path + '.' + RunStamp + '.old';", Whole("SetAsideIn")); // the name it was set aside under
        Assert.Contains("if RestartWorkPending(ExpandConstant('{app}')) then Result := 'An update of PoolWatch, a setup of it that did not finish,", Body("NotRemovedText"));
        // The script's reading of Windows' list (MovesAtRestart, held whole by EachFunctionThatDecidesWhenFilesGoIsHeldWhole):
        // the pairs ReadRestartWork reads, each the path it moves, then where to ("!" first when it may replace a file),
        // or an empty string for a delete; Inno Setup gives the strings with #0 between them and drops the empty ones a
        // value ends in. Only a pair whose path is Path's and whose target is not empty counts: Path named as where another
        // file moves to does not, whatever follows it.
        Assert.Contains("Result := False; ReadRestartWork(Froms, Tos); for I := 0 to GetArrayLength(Froms) - 1 do if (Froms[I] = Lowercase(Path)) and (Tos[I] <> '') then Result := True;",
            Whole("MovesAtRestart"));
        const string app = @"C:\Program Files\PoolWatch";
        string aside = app + @"\PoolWatch.exe.20261104120000000.old", target = app + @"\PoolWatch.exe";
        string move = @"\??\" + aside + "\0!" + @"\??\" + target + "\0";
        string delete = @"\??\" + aside + "\0\0";
        string other = @"\??\C:\Windows\Temp\x.tmp" + "\0\0";
        Assert.True(ScriptMovesAtRestart(move, aside));
        Assert.True(ScriptMovesAtRestart(other + move, aside.ToUpperInvariant()));
        Assert.True(ScriptMovesAtRestart(move + delete, aside));
        Assert.False(ScriptMovesAtRestart(delete, aside));
        Assert.False(ScriptMovesAtRestart(delete.TrimEnd('\0'), aside));
        Assert.False(ScriptMovesAtRestart(delete + other, aside));
        Assert.False(ScriptMovesAtRestart(move, target)); // a target is not a file Windows moves
        Assert.False(ScriptMovesAtRestart(other, aside));
        // The set-aside name as another file's target (with no !, as another program may write it), with an entry after
        // it: not a move of that file.
        Assert.False(ScriptMovesAtRestart(@"\??\C:\x.tmp" + "\0" + @"\??\" + aside + "\0" + @"\??\C:\y.tmp" + "\0\0", aside));
    }

    [Fact]
    public void AFileOpenOnlyForAMomentDoesNotSendTheUpdateToTheRestart()
    {
        // A virus scanner or the search indexer opens a file only for a moment. Wherever the setup decides by a file in
        // use, it looks again for BriefHold seconds first: the last look before the files are replaced now (the first look
        // and the last are logged, and decide only when files were in use at every look between), and each file it sets
        // aside, before its rename and again under its new name (SetAsideSoon, FreeSoon). No such decision is made by one
        // look, nor by one rename: SetAsideSoon tries the rename again with each look, so a program that opens the file
        // between a look and the rename does not decide either.
        Assert.Contains("Result := CanReplace(Path); Quarter := 0; while not Result and (Quarter < BriefHold * 4) do begin Sleep(250); Quarter := Quarter + 1; " +
                        "Result := CanReplace(Path); end;", Whole("FreeSoon"));
        Assert.Contains("Result := False; Quarter := 0; repeat if Quarter > 0 then Sleep(250); if CanReplace(Path) then Result := RenameFile(Path, Aside); " +
                        "Quarter := Quarter + 1; until Result or (Quarter > BriefHold * 4);", Whole("SetAsideSoon"));
        Assert.Contains("Held := HeldFiles(App, True) > 0; Uninstaller := HeldUninstaller; Quarter := 0; while Held and (Quarter < BriefHold * 4) do begin Sleep(250); " +
                        "Quarter := Quarter + 1; Held := HeldFiles(App, Quarter = BriefHold * 4) > 0; Uninstaller := Uninstaller or HeldUninstaller; end; " +
                        "HeldUninstaller := Uninstaller; if Held then", Whole("WhenToReplace"));
        Assert.Contains("if SetAsideSoon(Path, Aside) then", Whole("SetAsideIn"));
        Assert.Contains("if not FreeSoon(AsideNames[I]) then Blocked := AsideNames[I];", Whole("SetAllAside"));
        // None of the three asks a file once, without that wait.
        foreach (var name in new[] { "SetAsideIn", "SetAllAside", "WhenToReplace" })
            Assert.DoesNotMatch(@"\bCanReplace\(", Whole(name));
    }

    [Fact]
    public void AFailedSetupPutsBackTheUninstallerItReplacedAndTheAppsAndFeaturesEntry()
    {
        // Inno Setup replaces the uninstaller's program in the install folder, then deletes the Apps and Features entry and
        // writes it anew, then saves the uninstaller's record: a step after the first two that fails (another program opened
        // the record after the setup's look, say) rolls back, and the rollback deletes the new entry and leaves the
        // replaced program. So the setup copies both before any file (ssInstall) and puts them back when it did not finish.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + Regex.Match(Section("Setup"), @"(?m)^AppId=\{\{([0-9A-F-]+)\}").Groups[1].Value + "}_is1",
            Constant("UninstallEntryKey"));
        string save = Whole("SaveEntryBefore");
        Assert.Contains("EntrySaved := RegGetValueNames(HKLM, UninstallEntryKey, Names); if not EntrySaved then Exit;", save);
        Assert.Contains("if RegQueryStringValue(HKLM, UninstallEntryKey, Names[I], Text) then SaveEntryValue(Names[I], Text, 0, False) " +
                        "else if RegQueryDWordValue(HKLM, UninstallEntryKey, Names[I], Number) then SaveEntryValue(Names[I], '', Number, True)", save);
        string put = Whole("PutEntryBack");
        // Written back whenever it is not as it was: deleted by the rollback, or left part written by a step that failed
        // part way (Inno Setup deletes the old entry first, and logs the new one for its rollback only after its last
        // value). What is there goes first, so no value of the new entry stays.
        Assert.Contains("if not EntrySaved or EntryAsSaved then Exit; Done := True; if RegKeyExists(HKLM, UninstallEntryKey) then " +
                        "Done := RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey);", put);
        Assert.Contains("if EntryIsNumber[I] then Done := RegWriteDWordValue(HKLM, UninstallEntryKey, EntryNames[I], EntryNumber[I]) and Done " +
                        "else Done := RegWriteStringValue(HKLM, UninstallEntryKey, EntryNames[I], EntryText[I]) and Done;", put);
        string same = Whole("EntryAsSaved");
        Assert.Contains("Result := RegGetValueNames(HKLM, UninstallEntryKey, Names) and (GetArrayLength(Names) = EntryCount);", same);
        Assert.Contains("if Result then begin if EntryIsNumber[I] then Result := RegQueryDWordValue(HKLM, UninstallEntryKey, EntryNames[I], Number) and " +
                        "(Number = EntryNumber[I]) else Result := RegQueryStringValue(HKLM, UninstallEntryKey, EntryNames[I], Text) and (Text = EntryText[I]); end;", same);
        Assert.Contains("if not EntrySaved then Exit; EntryCount := GetArrayLength(Names);", save);
        // A fresh install that stops part way through writing its entry: there was none before, and the rollback deletes
        // the entry only once its last value is written, so what is there is this run's, and goes. Noted before the
        // values are read; only SaveEntryBefore sets it, at ssInstall, so a setup cancelled before then deletes nothing.
        Assert.StartsWith("procedure SaveEntryBefore; var Names: TArrayOfString; Text: String; Number: Cardinal; I: Integer; begin " +
                          "EntryNew := not RegKeyExists(HKLM, UninstallEntryKey); EntrySaved := ", save);
        Assert.Single(Regex.Matches(Code(), @"\bEntryNew := "));
        Assert.Contains("begin if EntryNew then begin if RegKeyExists(HKLM, UninstallEntryKey) then if RegDeleteKeyIncludingSubkeys(HKLM, UninstallEntryKey) then " +
                        "Log('Deleted what Setup wrote of an Apps and Features entry, where there was none before the setup.') else Log(", put);
        Assert.True(put.IndexOf("if EntryNew then", StringComparison.Ordinal) < put.IndexOf("if not EntrySaved or EntryAsSaved then Exit;", StringComparison.Ordinal), put);
        // The uninstaller's program is copied back; one that cannot be (another program has it open) is said to be as
        // it was only when it is, as Inno Setup may have stopped before it replaced it.
        string back = Whole("PutUninstallerBack");
        Assert.Contains("if FileCopy(Saved, Path, False) then Log('Put back ' + Path + ' as it was before the setup.') else try " +
                        "if GetSHA256OfFile(Path) = GetSHA256OfFile(Saved) then Log(Path + ' could not be copied over' + HoldersOf(Path) + ', and is as it was before the setup.') " +
                        "else Log('Could not put back ' + Path + ' as it was before the setup' + HoldersOf(Path) + '.');", back);
        string saveUninstaller = Whole("SaveUninstallerBefore");
        Assert.Contains("SetArrayLength(UninstallerBefore, 0); if CompareText(UninstallDir(''), App) <> 0 then Exit; if FindFirst(App + '\\unins*', Find) then", saveUninstaller);
        Assert.Contains("((CompareText(Ext, '.exe') = 0) or (CompareText(Ext, '.msg') = 0))", saveUninstaller);
        Assert.Contains("if ForceDirectories(ExtractFileDir(Saved)) and FileCopy(App + '\\' + Find.Name, Saved, False) then", saveUninstaller);
        Assert.Contains("Path := App + '\\' + UninstallerBefore[I]; Saved := ExpandConstant('{tmp}\\uninstaller-before\\') + UninstallerBefore[I];", back);
        Assert.Contains("Saved := ExpandConstant('{tmp}\\uninstaller-before\\') + Find.Name;", saveUninstaller);
        // Copied after SetAllAside, which may send the setup to the restart and so its uninstaller to a folder of its own.
        Assert.Contains("SaveLinkBefore; SaveEntryBefore; SetAllAside(App); SaveUninstallerBefore(App);", Whole("CurStepChanged"));
        Assert.Contains("PutAllBack(Later); if not FinishAtRestart then DropNewFolders(App, Later); PutUninstallerBack; PutEntryBack;", Whole("DeinitializeSetup"));
    }

    [Fact]
    public void TheSetupAndTheUninstallerNeverAskForARestart()
    {
        // A silent setup restarts the PC by itself when Inno Setup's restart state is set (unless /NORESTART was given), so
        // nothing here sets it: no restartreplace or uninsrestartdelete flag, no NeedsRestart, no UninstallNeedRestart,
        // and RestartIfNeededByRun=no, so a change another program makes to Windows' rename list while a [Run] entry runs
        // asks for nothing either. Files go at the restart only through Inno Setup's RestartReplace, which leaves that
        // state alone. Inno Setup's own check for an unfinished earlier install, which would ask for one, is the subject of
        // the next test.
        string script = Script();
        Assert.Matches(@"(?m)^RestartIfNeededByRun=no\r?$", Section("Setup"));
        Assert.DoesNotMatch(@"(?im)^\s*AlwaysRestart\s*=\s*yes", script);
        Assert.DoesNotMatch(@"(?i)\brestartreplace\b", Section("Files"));
        Assert.DoesNotMatch(@"(?i)\buninsrestartdelete\b", script);
        Assert.DoesNotMatch(@"NeedsRestart\s*:=", script);
        Assert.DoesNotMatch(@"(?im)^\s*function\s+(UninstallNeedRestart|NeedRestart)\b", script);
        Assert.DoesNotContain("MoveFileEx@", script);
        Assert.DoesNotMatch(@"RegWrite\w*Value\(HKLM, SessionManagerKey", script);
        var calls = Regex.Matches(Code(), @"\bRestartReplace\(");
        Assert.Equal(21, calls.Count);
        foreach (var (name, count) in new[] { ("ScheduleMoves", 2), ("SwapAtRestart", 2), ("RemoveOldLayout", 2), ("PutBack", 2), ("KeepLink", 1),
                                              ("DeleteNowOrAtRestart", 1), ("MoveBackAtRestart", 1), ("Unstage", 2), ("CurStepChanged", 1),
                                              ("DeleteOneAtRestart", 1), ("TakeBack", 2), ("HoldUntilRestart", 1), ("DropNewFolders", 1),
                                              ("DropOldLayoutAside", 1), ("SetAsideAtRestart", 1) })
            Assert.True(Regex.Matches(Whole(name), @"\bRestartReplace\(").Count == count, name);
    }

    [Fact]
    public void ASetupRunAgainBeforeTheRestartIsNeverSentToInnoSetupsOwnRestart()
    {
        // Inno Setup's own check for an unfinished earlier install (PreviousInstallCompleted) runs before the script's
        // PrepareToInstall, and expands every [Files] destination: one that is in Windows' rename list for the next restart
        // makes it ask for a restart, which a silent setup without /NORESTART carries out. So the script decides at the
        // first expansion: ProgramDir reads Windows' list first, and when it has work in the install folder, every
        // destination is this run's own folder (named by the time), which is in no list. WhenToReplace reads it too, for a
        // run where that check had nothing to look at. Every [Files] destination of PoolWatch's goes through ProgramDir.
        Assert.Equal(OneLine("""
            function ProgramDir(Param: String): String;
            begin
              LookForRestartWork(ExpandConstant('{app}'));
              if FinishAtRestart then
                Result := StageDir
              else
                Result := ExpandConstant('{app}');
            end;
            """), Whole("ProgramDir"));
        Assert.Contains("if RestartWorkLooked then Exit; RestartWorkLooked := True; if RestartWorkPending(App) then " +
                        "FinishAtNextRestart('An earlier setup or uninstall of PoolWatch finishes when Windows restarts, so this one does too, after it.');",
            Whole("LookForRestartWork"));
        Assert.StartsWith("procedure WhenToReplace(const App: String); var Held, Uninstaller: Boolean; Quarter: Integer; begin LookForRestartWork(App);",
            Whole("WhenToReplace"));
        var entries = Regex.Matches(Section("Files"), @"(?m)^Source: ""\{#PublishDir\}.*$");
        Assert.Equal(9, entries.Count); // the build's 8, and the notices' icon (poolwatch.png, TrayAppNoticeTests)
        Assert.All(entries, e => Assert.Matches(@"; DestDir: ""\{code:ProgramDir\}(\\app)?"";", e.Value));
        Assert.DoesNotContain("DestDir: \"{app}", Script());
        Assert.DoesNotMatch(@"(?m)^\[(InstallDelete|Dirs)\]", Script()); // nothing else Inno Setup's check would look at
        // Windows' list: both values (PendingFileRenameOperations, and ...2 when the first is full), for the install
        // folder, as \??\ and its path, the form in which Inno Setup's check compares them.
        Assert.Equal(@"SYSTEM\CurrentControlSet\Control\Session Manager", Constant("SessionManagerKey"));
        Assert.Contains("Result := WorkFor('PendingFileRenameOperations', Lowercase('\\??\\' + App)) or WorkFor('PendingFileRenameOperations2', Lowercase('\\??\\' + App));",
            Body("RestartWorkPending"));
        Assert.Contains("if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then begin Work := Lowercase(Work) + #0; " +
                        "Result := (Pos(Folder + '\\', Work) > 0) or (Pos(Folder + #0, Work) > 0); end;", Body("WorkFor"));
        // This run's folder is named by the time the run started, to the millisecond, so no earlier run's entry names it.
        Assert.Contains("StageDir := ExpandConstant('{app}') + '\\' + StageFolder + '\\' + RunStamp;", Body("FinishAtNextRestart"));
        Assert.Contains("if Stamp = '' then Stamp := GetDateTimeString('yyyymmddhhnnsszzz', #0, #0); Result := Stamp;", Body("RunStamp"));
    }

    [Fact]
    public void ASetupThatFinishesAtTheNextRestartSaysSoAndExitsWithWindowsOwnCode()
    {
        // 3010 is Windows' "done; a restart completes it" (ERROR_SUCCESS_REBOOT_REQUIRED), which deployment tools know;
        // 1603 its "the install failed" (ERROR_INSTALL_FAILURE), for the case where Windows could not be given the files.
        Assert.Equal(3010, Number("ExitFinishesAtRestart"));
        Assert.Equal(1603, Number("ExitNotFinished"));
        Assert.Contains("Result := 0; if FinishAtRestart then begin if NotScheduled > 0 then Result := ExitNotFinished else Result := ExitFinishesAtRestart; end;",
            Body("GetCustomSetupExitCode"));
        // At the end of the install the files are given to Windows, and the log says what the page says, with every file
        // that was in use.
        Assert.Contains("if FinishAtRestart then begin SwapAtRestart; Log(FinishHeading + '. ' + FinishText + FinishFiles); if NotScheduled = 0 then",
            Body("CurStepChanged"));
        // The Finished page: its heading and its text say the update finishes at the restart, that the version that was
        // there stays until then and that the setup does not restart Windows, then why: each reason; "Start PoolWatch now"
        // is not offered (the new version is not in place yet), and where its tick box would be, a list that scrolls names
        // every file in use with what has it open, however many there are (FinishFiles: every line HeldAll had).
        string page = Body("LayOutFinishedPage");
        Assert.Contains("if FinishAtRestart then begin WizardForm.FinishedHeadingLabel.Caption := FinishHeading; WizardForm.FinishedLabel.Caption := FinishText + #13#10; end " +
                        "else WizardForm.FinishedLabel.Caption := SetupMessage(msgFinishedLabel) + #13#10;", page);
        Assert.Contains("if FinishAtRestart and (FinishFiles <> '') then begin Files := TNewMemo.Create(WizardForm); Files.Parent := WizardForm.FinishedPage; " +
                        "Files.Left := WizardForm.RunList.Left; Files.Top := WizardForm.RunList.Top; Files.Width := WizardForm.RunList.Width; " +
                        "Files.Height := WizardForm.RunList.Height;", page);
        Assert.Contains("Files.ReadOnly := True; Files.ScrollBars := ssVertical; Files.Text := Copy(FinishFiles, 3, Length(FinishFiles));", page);
        Assert.DoesNotContain("MaxShown", page);
        Assert.Contains("AddShown(HeldList, HeldCount, Rel + Name + Holders); HeldAll := HeldAll + #13#10 + Rel + Name + Holders;", Whole("FindHeld"));
        Assert.Contains("HeldList := ''; HeldAll := '';", Whole("HeldFiles"));
        Assert.Equal(2, Regex.Matches(Code(), @"\bFinishFiles := HeldAll;").Count); // WhenToReplace's and SetAllAside's
        Assert.DoesNotContain("HeldList", Whole("WhenToReplace"));
        Assert.DoesNotContain("HeldList", Whole("SetAllAside"));
        Assert.Equal(OneLine("""
            function FinishHeading: String;
            begin
              if NotScheduled > 0 then
                Result := 'PoolWatch is not fully updated'
              else
                Result := 'PoolWatch updates when Windows restarts';
            end;
            """), Whole("FinishHeading"));
        string text = Whole("FinishText");
        int failed = text.IndexOf("if NotScheduled > 0 then begin Result := 'Windows could not be given the new files to put in place at its next restart, so Setup took them back; ' + " +
                                  "Meanwhile + '.';", StringComparison.Ordinal);
        int rest = text.IndexOf("Result := Result + ' Until the setup has run again, Apps > Installed apps shows this version''s number. Restart Windows, ' + " +
                                "'then run the setup again: it then puts the whole version in place. Its log says what happened.' + #13#10#13#10 + " +
                                "FinishReason; end", StringComparison.Ordinal);
        int done = text.IndexOf("else Result := 'Windows puts this version of PoolWatch in place the next time it restarts; ' + Meanwhile + '. Setup does ' + " +
                                "'not restart Windows: restart it when it suits you.' + #13#10#13#10 + FinishReason;", StringComparison.Ordinal);
        Assert.True(failed > 0 && rest > failed && done > rest, text);
        // What starts PoolWatch until the restart is read from the install folder (UntilRestart, the next test), so the
        // page is true after an earlier setup or uninstall that left work for that restart, not only after this run's.
        Assert.StartsWith("function FinishText: String; var Meanwhile: String; begin Meanwhile := 'until then ' + UntilRestart(ExpandConstant('{app}'));", text);
        Assert.Matches(@"(?m)^Filename: ""\{app\}\\PoolWatch\.exe""; Description: ""Start PoolWatch now""; .*; Check: StartsNow\r?$", Section("Run"));
        Assert.Contains("Result := not FinishAtRestart;", Body("StartsNow"));
        // Each reason is added, never replaces an earlier one (an earlier setup's work and a file in use, say).
        Assert.Contains("if FinishReason <> '' then FinishReason := FinishReason + #13#10#13#10; FinishReason := FinishReason + Why;", Whole("FinishAtNextRestart"));
        Assert.Contains("if Held then begin FinishFiles := HeldAll; FinishAtNextRestart('Some of PoolWatch''s files are in use, so they cannot be replaced now. " +
                        "Each is listed below with what has it open.'); end", Whole("WhenToReplace"));
        // Each file in use is named with what has it open, in the list the page shows and in the log.
        string held = Whole("FindHeld");
        Assert.Contains("Holders := ''; if Logged then Holders := HoldersOf(Path); AddShown(HeldList, HeldCount, Rel + Name + Holders); " +
                        "HeldAll := HeldAll + #13#10 + Rel + Name + Holders; if Logged then Log('In use, so it cannot be replaced or removed now: ' + Path + Holders);", held);
        // The look that decides when files are still in use is logged: the last one, after BriefHold seconds.
        Assert.Contains("Held := HeldFiles(App, Quarter = BriefHold * 4) > 0;", Whole("WhenToReplace"));
        // The setup always writes a log (in the TEMP folder of the account that runs it), so a run started by hand has one
        // too. The uninstaller writes one only when it is started with /LOG, as the Apps and Features entry starts it.
        Assert.Matches(@"(?m)^SetupLogging=yes\r?$", Section("Setup"));
        Assert.Matches(@"(?m)^UninstallLogging=yes\r?$", Section("Setup"));
    }

    /// <summary>
    /// A value of Windows' list as Inno Setup's RegQueryMultiStringValue gives it to the script: the REG_MULTI_SZ data
    /// with every #0 it ends in dropped and one put back (none when it is empty).
    /// </summary>
    private static string AsInnoGivesIt(string data)
    {
        string trimmed = data.TrimEnd('\0');
        return trimmed.Length == 0 ? "" : trimmed + "\0";
    }

    /// <summary>A copy of the script's ListedPath.</summary>
    private static string ScriptListedPath(string item)
    {
        if (item.Length >= 2 && item[0] == '*' && (item[1] == '1' || item[1] == '2')) item = item[2..];
        if (item.StartsWith('!')) item = item[1..];
        if (item.Length >= 2 && item[0] == '*' && (item[1] == '1' || item[1] == '2')) item = item[2..];
        if (item.StartsWith(@"\??\", StringComparison.Ordinal)) item = item[4..];
        return item;
    }

    /// <summary>A copy of the script's ReadRestartWork over the data of Windows' two values, as Windows writes them.</summary>
    private static List<(string From, string To)> ScriptRestartWork(params string[] values)
    {
        var pairs = new List<(string From, string To)>();
        foreach (string data in values)
        {
            string work = AsInnoGivesIt(data).ToLowerInvariant();
            bool isTo = false;
            int start = 1; // the script's strings count from 1
            for (int p = 1; p <= work.Length + 1; p++)
            {
                char c = p <= work.Length ? work[p - 1] : '\0'; // CharAt
                if (c != '\0' || (p > work.Length && start > work.Length)) continue;
                string item = ScriptListedPath(work.Substring(start - 1, p - start));
                start = p + 1;
                if (isTo) pairs[^1] = (pairs[^1].From, item);
                else pairs.Add((item, ""));
                isTo = !isTo;
            }
        }
        return pairs;
    }

    /// <summary>A copy of the script's DeletedAtRestart, which a test holds to Windows' list as Windows writes it.</summary>
    private static bool ScriptDeletedAtRestart(string data, string path) =>
        ScriptRestartWork(data).Any(pair => pair.From == path.ToLowerInvariant() && pair.To == "");

    /// <summary>A copy of the script's FilledAtRestart, with the files that are there now given as a set.</summary>
    private static string ScriptFilledAtRestart(string data, string path, string stageDir, ISet<string> there)
    {
        string result = there.Contains(path) ? path : "";
        string name = path.ToLowerInvariant(), own = stageDir.ToLowerInvariant() + @"\";
        foreach (var (from, to) in ScriptRestartWork(data))
        {
            if (from.StartsWith(own, StringComparison.Ordinal)) continue;
            if (to == name) { if (there.Contains(from)) result = from; }
            else if (from == name) result = "";
        }
        return result;
    }

    [Fact]
    public void WhatTheSetupSaysStartsPoolWatchUntilTheRestartIsReadFromTheInstallFolder()
    {
        // A setup run again before the restart finishes at that restart too, after an earlier setup's or uninstall's work,
        // and the install folder is then as that work left it: after an uninstall, PoolWatch is not installed; after a
        // setup that did not finish, no launcher may be there, or a new launcher and program another program keeps from
        // being taken back (which Windows deletes at the restart, or, when it would not take that, which stay after it:
        // LeftNewHas), or one of the launcher's names may go back only then. So the Finished page and the box of a setup
        // that did not finish read it from the folder and Windows' list, in that order of cases, once this run has done
        // what it does now: never from what this run did alone.
        string until = Whole("UntilRestart");
        int gone = until.IndexOf("if DeletedAtRestart(App) then Result := 'PoolWatch is not installed: an earlier uninstall removed it, and Windows deletes " +
                                 "what is left ' + 'of it at that restart'", StringComparison.Ordinal);
        int starter = until.IndexOf("Starter := LauncherPath(App); if (Starter = '') and not HasLauncher(App) and FileExists(LauncherFile(App)) and " +
                                    "not DeletedAtRestart(LauncherFile(App)) and not LeftNewHas(LauncherFile(App)) then Starter := LauncherFile(App);",
                                    StringComparison.Ordinal);
        int none = until.IndexOf("if Starter = '' then Result := 'PoolWatch does not start: no file that starts it is in place'", StringComparison.Ordinal);
        int held = until.IndexOf("else if DeletedAtRestart(Starter) then Result := 'what starts PoolWatch starts a launcher and a program that another " +
                                 "program keeps from ' + 'being taken back, without the other files of their version: Windows deletes that launcher at that " +
                                 "restart'", StringComparison.Ordinal);
        int left = until.IndexOf("else if LeftNewHas(Starter) then Result := 'what starts PoolWatch starts a launcher and a program that another program " +
                                 "keeps from ' + 'being taken back, without the other files of their version, and Windows would not take that launcher to ' + " +
                                 "'delete at that restart, so it stays after it'", StringComparison.Ordinal);
        // A launcher's name that goes back only then is said to start nothing only while it holds no file now: one that
        // holds a file (an old launcher another program keeps in place, with a copy of it Windows moves back after a held
        // new launcher's move) starts that file until then.
        int name = until.IndexOf("else begin if (NameGoesBack(App, PoolWatchName) and not FileExists(App + '\\' + PoolWatchName)) or " +
                                 "(NameGoesBack(App, SelfContainedName) and not FileExists(SecondLauncherPath(App))) then Result := 'the version you had " +
                                 "stays in place, but for one of the names of its launcher, which ' + 'Windows puts back at that restart: what names that " +
                                 "file starts nothing until then'", StringComparison.Ordinal);
        int stays = until.IndexOf("else Result := 'the version you had stays in place';", StringComparison.Ordinal);
        // A file of that version Unstage set aside now, as it could keep no copy of it (TakenOut), is said too.
        int taken = until.IndexOf("if TakenOut > 0 then Result := Result + '; Setup set some of its files aside so that the restart does not replace them " +
                                  "(listed ' + 'below), so it may not start until then'; end; end; end;", StringComparison.Ordinal);
        Assert.True(gone > 0 && starter > gone && none > starter && held > none && left > held && name > left && stays > name && taken > stays, until);
        Assert.Single(Regex.Matches(Code(), @"\bTakenOut := TakenOut \+ 1;"));
        Assert.Equal("function LeftNewHas(const Path: String): Boolean; begin Result := Pos(#0 + Lowercase(Path) + #0, #0 + LeftNew) > 0; end;", Whole("LeftNewHas"));
        // Both callers: the Finished page, and the box of a setup that did not finish, after every put-back.
        Assert.Contains("Meanwhile := 'until then ' + UntilRestart(ExpandConstant('{app}'));", Whole("FinishText"));
        string deinit = Whole("DeinitializeSetup");
        Assert.Contains("Text := Text + '. Until that restart ' + UntilRestart(App) + '.';", deinit);
        Assert.True(deinit.IndexOf("UntilRestart(App)", StringComparison.Ordinal) > deinit.IndexOf("HoldOldUninstaller(App);", StringComparison.Ordinal), deinit);
        Assert.Equal(2, Regex.Matches(Code(), @"(?<!function )\bUntilRestart\(").Count);
        // The script's reading of Windows' list (ReadRestartWork, ListedPath and DeletedAtRestart, held whole by
        // EachFunctionThatDecidesWhenFilesGoIsHeldWhole): pairs of strings, a path then where it moves to, '' for a
        // delete. A move to Path (with or without the ! that lets it replace a file), a move from Path and another file
        // whose name starts with Path's do not count; Inno Setup gives the strings with #0 between them and drops the
        // empty ones the value ends in.
        Assert.Contains("ReadRestartWork(Froms, Tos); for I := 0 to GetArrayLength(Froms) - 1 do if (Froms[I] = Lowercase(Path)) and (Tos[I] = '') then Result := True;",
            Whole("DeletedAtRestart"));
        string read = Whole("ReadRestartWork");
        // One pass over each value (no copy of what is left of it per string), the arrays grown by half again at a time
        // and cut to the pairs read at the end, so a long list of other programs' work costs time in proportion to it.
        Assert.Contains("if RegQueryMultiStringValue(HKLM, SessionManagerKey, ValueName, Work) then begin Work := Lowercase(Work); IsTo := False; Start := 1; " +
                        "for P := 1 to Length(Work) + 1 do if CharAt(Work, P) = #0 then if (P <= Length(Work)) or (Start <= Length(Work)) then begin " +
                        "Item := ListedPath(Copy(Work, Start, P - Start)); Start := P + 1; if IsTo then Tos[N - 1] := Item else begin " +
                        "if N = GetArrayLength(Froms) then begin SetArrayLength(Froms, N + N div 2 + 16); SetArrayLength(Tos, N + N div 2 + 16); end; " +
                        "N := N + 1; Froms[N - 1] := Item; Tos[N - 1] := ''; end; IsTo := not IsTo; end; end; end; SetArrayLength(Froms, N); SetArrayLength(Tos, N); end;", read);
        Assert.DoesNotContain("Work := Copy(", read);
        Assert.Contains("for V := 1 to 2 do begin if V = 1 then ValueName := 'PendingFileRenameOperations' else ValueName := 'PendingFileRenameOperations2';", read);
        Assert.Contains("if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then Result := Copy(Result, 3, Length(Result)); " +
                        "if CharAt(Result, 1) = '!' then Result := Copy(Result, 2, Length(Result)); if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or " +
                        "(CharAt(Result, 2) = '2')) then Result := Copy(Result, 3, Length(Result)); if Copy(Result, 1, 4) = '\\??\\' then Result := Copy(Result, 5, Length(Result));",
            Whole("ListedPath"));
        const string app = @"C:\Program Files\PoolWatch";
        string launcher = app + @"\PoolWatch.exe", staged = app + @"\at-restart\20261104120000000\PoolWatch.exe";
        string delete = @"\??\" + launcher + "\0\0";
        string moveTo = @"\??\" + staged + "\0!" + @"\??\" + launcher + "\0";
        string moveFrom = @"\??\" + launcher + "\0!" + @"\??\" + app + @"\PoolWatch.exe.1.old" + "\0";
        string longer = @"\??\" + launcher + ".20261104120000000.new\0\0";
        Assert.True(ScriptDeletedAtRestart(delete + "\0", launcher));
        Assert.True(ScriptDeletedAtRestart(delete, launcher.ToUpperInvariant()));
        Assert.True(ScriptDeletedAtRestart(moveTo + delete + "\0", launcher));
        Assert.True(ScriptDeletedAtRestart(delete + moveTo + "\0", launcher));
        Assert.True(ScriptDeletedAtRestart(@"\??\" + app + "\0\0\0", app));
        Assert.False(ScriptDeletedAtRestart(moveTo + "\0", launcher));
        Assert.False(ScriptDeletedAtRestart(moveFrom + "\0", launcher));
        Assert.False(ScriptDeletedAtRestart(longer + "\0", launcher));
        Assert.False(ScriptDeletedAtRestart(delete + "\0", app));
        // A move to Path whose target has no ! (another program's MoveFileEx without MOVEFILE_REPLACE_EXISTING), last in the
        // list, is a move, not a delete: the pairs keep each target with its path.
        Assert.False(ScriptDeletedAtRestart(@"\??\" + staged + "\0" + @"\??\" + launcher + "\0\0", launcher));
        Assert.False(ScriptDeletedAtRestart(@"\??\C:\x.tmp" + "\0\0" + @"\??\" + staged + "\0" + @"\??\" + launcher + "\0\0", launcher));
        // The pairs, in order, as Windows writes them: a delete's empty target, a move's !, the *1 and *2 some writers add.
        Assert.Equal([(staged.ToLowerInvariant(), launcher.ToLowerInvariant()), (launcher.ToLowerInvariant(), ""), (@"c:\x.tmp", @"c:\y.tmp")],
            ScriptRestartWork(moveTo + delete + @"*1\??\C:\x.tmp" + "\0" + @"!*2\??\C:\y.tmp" + "\0\0"));
        Assert.Equal([(staged.ToLowerInvariant(), ""), (app.ToLowerInvariant(), "")], ScriptRestartWork(@"\??\" + staged + "\0\0\0", @"\??\" + app + "\0\0\0"));
        Assert.Empty(ScriptRestartWork("", "\0\0"));
        // As recent Windows builds write every entry (seen on a CI runner): *1 or *2 first, before a move target's !. The
        // move into the launcher's place is read with its target, so FilledAtRestart and KeepLauncherOut find it.
        string moveTo25H2 = @"*1\??\" + staged + "\0" + @"*1!\??\" + launcher + "\0";
        Assert.Equal([(staged.ToLowerInvariant(), launcher.ToLowerInvariant()), (launcher.ToLowerInvariant(), "")],
            ScriptRestartWork(moveTo25H2 + @"*2\??\" + launcher + "\0\0"));
        Assert.True(ScriptDeletedAtRestart(moveTo25H2 + @"*2\??\" + launcher + "\0\0", launcher));
        Assert.False(ScriptDeletedAtRestart(moveTo25H2, launcher));
        Assert.Equal(staged.ToLowerInvariant(), ScriptFilledAtRestart(moveTo25H2, launcher, app + @"\at-restart\20261104120001000",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { staged }));
        // The uninstall that leaves work for the restart gives Windows the install folder itself to delete.
        Assert.Contains("if DirExists(App) then DeleteAtRestart(App, '');", Whole("RemoveRest"));
        Assert.Contains("DeleteOneAtRestart(Dir, '', '');", Whole("DeleteAtRestart"));
    }

    [Fact]
    public void TheNewFilesGoInPlaceAllTogetherAtTheRestartOrNoneDoes()
    {
        // Each file set aside is given to Windows to move over its old self at the restart, then each folder (once empty)
        // to delete; then the Start menu entry an earlier uninstall gave Windows to delete is moved back, the files that
        // keep folders until the moves go, and StageFolder last. If Windows could not be given one file, the new files
        // and the keeping files are deleted, so the restart moves none. A new file that cannot be deleted (another program
        // has it open) still has its move in Windows' list: it is renamed, so that the move finds nothing, and deleted then;
        // one that cannot be renamed either moves at the restart, and what it replaces then is kept now, as a copy or, when
        // it cannot be read, under a second name of the same file (CreateHardLinkW), and moved back after it. What it
        // replaces then is read from Windows' list too (FilledAtRestart): an old file an earlier setup that did not finish
        // left to go back there at the restart counts, so that place is never given to Windows to delete; only where
        // nothing would be there is it deleted after the move. So the restart puts none of them in place, but for one
        // whose move could not be undone (what it replaces could be neither copied nor given a second name, or Windows
        // would not take the move back: MoveFileEx fails when it cannot write its list): that one is counted (Unkept) and
        // named on the Finished page, which says the restart puts it among the old files; the file it would replace is
        // renamed aside now, so that it is not lost (TakenOut, named too, when it is a file in place now), and no file that
        // starts PoolWatch is left at the launcher's names after the restart (KeepLauncherOut): each one Windows' list would
        // move there and the one there now are renamed aside now (a rename needs nothing from that list), and a name
        // whose file another program keeps from being renamed is given to Windows to rename aside at the restart, after
        // every move to it, while the file at that name is still there; only one Windows will not take that for is
        // counted and named (LauncherKept), and the page then says PoolWatch may start from old and new files mixed.
        string moves = Whole("ScheduleMoves");
        int keep = moves.IndexOf("KeepFolder(Target);", StringComparison.Ordinal);
        int file = moves.IndexOf("RestartReplace(Dir + '\\' + Name, Target + '\\' + Name);", StringComparison.Ordinal);
        int folder = moves.IndexOf("RestartReplace(Dir, '');", StringComparison.Ordinal);
        Assert.True(keep > 0 && file > keep && folder > file, moves);
        Assert.Contains("ForceDirectories(Target); RestartReplace(", moves);
        Assert.Contains("except NotScheduled := NotScheduled + 1;", moves);
        string swap = Whole("SwapAtRestart");
        Assert.Contains("NotScheduled := 0; ScheduleMoves(StageDir, App); KeepLink(App); for I := 0 to GetArrayLength(Keeps) - 1 do try RestartReplace(Keeps[I], ''); " +
                        "except", swap);
        Assert.Contains("try RestartReplace(App + '\\' + StageFolder, ''); except", swap);
        Assert.Contains("if NotScheduled > 0 then begin Log(Format(", swap);
        Assert.Contains("if not DelTree(StageDir, True, True, True) then begin Log('Some of the new files could not be deleted: ' + StageDir); " +
                        "Unstage(StageDir, App); if Unkept > 0 then KeepLauncherOut(App); end; for I := 0 to GetArrayLength(Keeps) - 1 do DeleteFile(Keeps[I]); end; end;", swap);
        string keepOut = Whole("KeepLauncherOut");
        // Each file Windows' list moves to that name, whoever gave it, is renamed aside first, so that its move finds nothing.
        Assert.Contains("ReadRestartWork(Froms, Tos); for I := 1 to 2 do begin if I = 1 then Path := App + '\\' + PoolWatchName else Path := SecondLauncherPath(App); " +
                        "Held := ''; for J := 0 to GetArrayLength(Froms) - 1 do if Tos[J] = Lowercase(Path) then if FileExists(Froms[J]) then " +
                        "if not RenameFile(Froms[J], Froms[J] + '.' + RunStamp + '.out.old') then Held := Froms[J];", keepOut);
        // When one of those is held, Windows is given the name to rename aside at the restart, after the moves to it, while
        // the file at that name is still there; only then is that file renamed aside now. A file at the name that is held
        // itself gives Windows the name the same way, unless it was given it already.
        int sources = keepOut.IndexOf("if not RenameFile(Froms[J], Froms[J] + '.' + RunStamp + '.out.old') then Held := Froms[J];", StringComparison.Ordinal);
        int later = keepOut.IndexOf("if Held <> '' then SetAsideAtRestart(Path, Held);", StringComparison.Ordinal);
        int now = keepOut.IndexOf("if FileExists(Path) then if not RenameFile(Path, Path + '.' + RunStamp + '.out.old') then if Held = '' then begin " +
                                  "Held := Path; SetAsideAtRestart(Path, Held); end;", StringComparison.Ordinal);
        Assert.True(sources > 0 && later > sources && now > later, keepOut);
        // No rename of the file at the name comes before Windows is asked (only the one at "now").
        Assert.Single(Regex.Matches(keepOut, @"RenameFile\(Path, "));
        Assert.True(keepOut.IndexOf("RenameFile(Path, ", StringComparison.Ordinal) > later, keepOut);
        Assert.Contains("if Held = '' then Log(", keepOut);
        Assert.Equal(2, Regex.Matches(keepOut, @"\bSetAsideAtRestart\(").Count);
        Assert.DoesNotContain("RestartReplace(", keepOut);
        // Counted only when Windows refuses that too, and named with the held file.
        string atRestart = Whole("SetAsideAtRestart");
        Assert.Contains("try RestartReplace(Path, Path + '.' + RunStamp + '.later.old'); Log(", atRestart);
        Assert.Contains("except LauncherKept := LauncherKept + 1; if CompareText(Held, Path) = 0 then FinishFiles := FinishFiles + #13#10 + Path + " +
                        "', which could not be set aside' + HoldersOf(Held) else FinishFiles := FinishFiles + #13#10 + Path + ', where Windows moves ' + Held + " +
                        "' at the restart: that file ' + 'could not be set aside' + HoldersOf(Held);", atRestart);
        Assert.Single(Regex.Matches(Code(), @"\bLauncherKept := LauncherKept \+ 1;"));
        Assert.DoesNotContain("DeleteFile(", keepOut); // nothing is deleted: what is set aside is kept
        Assert.Single(Regex.Matches(Code(), @"\bKeepLauncherOut\(App\);"));
        Assert.Equal(2, Regex.Matches(Code(), @"(?<!procedure )\bSetAsideAtRestart\(").Count);
        string unstage = Whole("Unstage");
        Assert.Contains("Path := Dir + '\\' + Name; Place := Target + '\\' + Name; if RenameFile(Path, Path + '.unmoved') then " +
                        "DeleteNowOrAtRestart(Path + '.unmoved') else begin Before := ''; Saved := ''; try Before := FilledAtRestart(Place); if Before <> '' then begin " +
                        "if not FileCopy(Before, Place + '.' + RunStamp + '.old', False) then if not CreateHardLinkW(Place + '.' + RunStamp + '.old', Before, 0) then " +
                        "RaiseException('it could be neither copied nor given a second name, ' + Place + '.' + RunStamp + '.old'); Saved := Place + '.' + RunStamp + '.old'; " +
                        "RestartReplace(Saved, Place); end else RestartReplace(Place, '');", unstage);
        Assert.Contains("except Unkept := Unkept + 1; FinishFiles := FinishFiles + #13#10 + Place + ', which Windows still moves into place at the restart' + " +
                        "HoldersOf(Path);", unstage);
        // The file the move would replace, of which no copy was kept, is renamed so that it is not replaced: one an earlier
        // entry would move there, and one in place now (counted, TakenOut, and named); one at a launcher's name is left to
        // KeepLauncherOut, which renames it once Windows has the name to set aside at the restart.
        Assert.Contains("if (Saved = '') and (Before <> '') and not StartsPoolWatch(Before) then if RenameFile(Before, Before + '.' + RunStamp + '.out.old') then " +
                        "begin if CompareText(Before, Place) = 0 then begin TakenOut := TakenOut + 1; FinishFiles := FinishFiles + #13#10 + Place + " +
                        "', which Setup set aside now, as ' + Before + '.' + RunStamp + '.out.old, so that the restart does not replace it'; Log(", unstage);
        Assert.Single(Regex.Matches(Code(), @"\bUnkept := Unkept \+ 1;"));
        Assert.Single(Regex.Matches(unstage, @"RestartReplace\(Place, ''\);"));
        // FilledAtRestart: what is there now, then each earlier entry about that place in Windows' order; this run's own moves
        // (out of StageDir) do not count.
        string filled = Whole("FilledAtRestart");
        Assert.Contains("Result := ''; if FileExists(Path) then Result := Path; Name := Lowercase(Path); Own := Lowercase(StageDir) + '\\'; ReadRestartWork(Froms, Tos); " +
                        "for I := 0 to GetArrayLength(Froms) - 1 do if Copy(Froms[I], 1, Length(Own)) <> Own then begin if Tos[I] = Name then begin " +
                        "if FileExists(Froms[I]) then Result := Froms[I]; end else if Froms[I] = Name then Result := ''; end;", filled);
        const string app = @"C:\Program Files\PoolWatch";
        string stage = app + @"\at-restart\20261105120000000", place = app + @"\LICENSE", aside = place + ".20261104120000000.old";
        string staged = stage + @"\LICENSE", earlier = app + @"\at-restart\20261104110000000\LICENSE";
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var asideThere = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { aside, staged };
        // A setup that did not finish (a new LICENSE held there, given to Windows to delete, and the old one to move back
        // after it), then this setup's own move: what this move replaces is the old file, not nothing.
        string afterFailed = @"\??\" + place + "\0\0" + @"\??\" + aside + "\0!" + @"\??\" + place + "\0" + @"\??\" + staged + "\0!" + @"\??\" + place + "\0\0";
        Assert.Equal(aside.ToLowerInvariant(), ScriptFilledAtRestart(afterFailed, place, stage, new HashSet<string>(asideThere, StringComparer.OrdinalIgnoreCase) { place }));
        Assert.Equal(aside.ToLowerInvariant(), ScriptFilledAtRestart(afterFailed, place, stage, asideThere));
        // An earlier setup that finishes at the restart: its staged file is what is there then.
        string afterEarlier = @"\??\" + earlier + "\0!" + @"\??\" + place + "\0" + @"\??\" + staged + "\0!" + @"\??\" + place + "\0\0";
        Assert.Equal(earlier.ToLowerInvariant(), ScriptFilledAtRestart(afterEarlier, place, stage, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { earlier, place }));
        // A move whose file is gone is skipped by Windows, so what was there stays; a delete leaves nothing.
        Assert.Equal(place, ScriptFilledAtRestart(afterEarlier, place, stage, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { place }));
        Assert.Equal("", ScriptFilledAtRestart(@"\??\" + place + "\0\0\0", place, stage, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { place }));
        Assert.Equal("", ScriptFilledAtRestart(@"\??\" + staged + "\0!" + @"\??\" + place + "\0\0", place, stage, none));
        string text = Whole("FinishText");
        // One file of either kind is said in the singular (it read "Setup set 1 files ... aside").
        Assert.Contains("if Unkept > 0 then begin if Unkept = 1 then Result := Result + ' But one of them, which another program has open (listed below), could not be taken back: ' + " +
                        "'Windows still puts it in place at that restart, among the files of the version you had.' " +
                        "else Result := Result + Format(' But %d of them, which another program has open (listed below), could not be taken ' + " +
                        "'back: Windows still puts those in place at that restart, among the files of the version you had.', [Unkept]); " +
                        "if TakenOut = 1 then Result := Result + ' Setup set one file of the version you had aside now (listed below), as it could keep no ' + " +
                        "'copy of it, so that the restart does not replace it.' " +
                        "else if TakenOut > 1 then Result := Result + Format(' Setup set %d files of the version you had aside now (listed below), as it could ' + " +
                        "'keep no copy of them, so that the restart does not replace them.', [TakenOut]); " +
                        "if LauncherKept > 0 then Result := Result + ' Another program also keeps one of the files that start PoolWatch from being set aside, ' + " +
                        "'and Windows would not take it to set aside at that restart either (listed below), so after that restart ' + 'PoolWatch may start from old " +
                        "and new files mixed until the setup has run again.' else Result := Result + ' So that PoolWatch never starts from old and new files mixed, " +
                        "Setup set aside the files ' + 'that start it, now or, where another program has one open, at that restart: after that restart PoolWatch ' + " +
                        "'does not start until the setup has run again.'; end;", text);
        Assert.DoesNotContain("would not take back the moves", text);
        Assert.Contains("Name := Copy(Name, 2, Length(Name) - 1); Unstage(Dir + '\\' + Name, Target + '\\' + Name);", unstage);
        // The folder is listed whole before any file in it is renamed, so a renamed file is never listed again.
        int listed = unstage.IndexOf("until not FindNext(Find); finally FindClose(Find); end;", StringComparison.Ordinal);
        Assert.True(listed > 0 && unstage.IndexOf("RenameFile(", StringComparison.Ordinal) > listed, unstage);
        Assert.Contains("end; for I := 0 to GetArrayLength(Found) - 1 do begin Name := Found[I]; if Copy(Name, 1, 1) = '\\' then", unstage);
        Assert.Equal("at-restart", Constant("StageFolder"));
        // Nothing replaces a file in that folder, so the check for files in use skips it.
        Assert.Contains("((Rel = '') and ((CompareText(Name, StageFolder) = 0) or (CompareText(Name, UninstallFolder) = 0)))", Body("FindHeld"));
    }

    [Fact]
    public void AFolderAnEarlierUninstallDeletesAtTheRestartIsKeptUntilTheMovesIntoIt()
    {
        // Windows works through its rename list in order at the restart, and deletes a folder only while it is empty. An
        // uninstall that left a file in use gave Windows that file, its folder and the install folder to delete then; a
        // setup run before that restart comes after them in the list. So in each folder its files move into that is in the
        // list to delete, it leaves a file of its own (KeepName) until after its moves, or the folder would be gone before
        // them; a file it cannot make counts as a file Windows was not given, so that none moves. The Start menu entry, which
        // such an uninstall may have given Windows to delete too, is copied and moved back after that delete.
        Assert.Equal("at-restart.keep", Constant("KeepName"));
        string keep = Whole("KeepFolder");
        Assert.Contains("if not GoesAtRestart(Folder) then Exit; Keep := Folder + '\\' + KeepName; ForceDirectories(Folder); if SaveStringToFile(Keep, '', False) then", keep);
        Assert.Contains("Keeps[N] := Keep;", keep);
        Assert.Contains("else begin NotScheduled := NotScheduled + 1;", keep);
        // The list has the folder itself to delete, or to move elsewhere: a pair of it whose path is the folder's
        // (ReadRestartWork); the folder named as where another path moves to does not count, nor does a path in it, nor
        // another one whose name starts with the folder's.
        Assert.Contains("Result := False; ReadRestartWork(Froms, Tos); for I := 0 to GetArrayLength(Froms) - 1 do if Froms[I] = Lowercase(Path) then Result := True;",
            Whole("GoesAtRestart"));
        const string app = @"C:\Program Files\PoolWatch";
        string folder = app + @"\app", lnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PoolWatch.lnk";
        string staged = app + @"\at-restart\20261104120000000\PoolWatch.lnk";
        Assert.True(ScriptGoesAtRestart(@"\??\" + folder + "\0\0", folder));
        Assert.True(ScriptGoesAtRestart(@"\??\" + app + @"\app\PoolWatch.exe" + "\0\0" + @"\??\" + folder + "\0", folder.ToUpperInvariant()));
        Assert.True(ScriptGoesAtRestart(@"\??\" + lnk + "\0\0", lnk));
        Assert.False(ScriptGoesAtRestart(@"\??\" + staged + "\0!" + @"\??\" + lnk + "\0\0", lnk)); // a move to it (an earlier setup's KeepLink)
        Assert.False(ScriptGoesAtRestart(@"\??\" + app + @"\app\PoolWatch.exe" + "\0\0", folder));
        Assert.False(ScriptGoesAtRestart(@"\??\" + folder + "x\0\0", folder));
        // The list as another program may write it: a move to the folder's name whose target has no ! is still a target.
        Assert.False(ScriptGoesAtRestart(@"\??\C:\x.tmp" + "\0" + @"\??\" + folder + "\0\0", folder));
        string link = Whole("KeepLink");
        Assert.Contains("Link := ExpandConstant('{autoprograms}\\PoolWatch.lnk'); if not FileExists(Link) or not GoesAtRestart(Link) then Exit; " +
                        "Saved := App + '\\' + StageFolder + '\\' + RunStamp + '.lnk';", link);
        Assert.Contains("RestartReplace(Saved, Link); Result := True;", link);
        // In both kinds of setup: after the moves when it finishes at the restart, after its files are in place otherwise.
        Assert.Contains("if KeepLink(App) then try RestartReplace(App + '\\' + StageFolder, '');", Whole("CurStepChanged"));
        Assert.Contains("KeepLink(App);", Whole("SwapAtRestart"));
        // A file a setup kept a folder with, found by a later in-place setup, goes with the old files.
        Assert.Contains("(CompareText(ExtractFileName(AsideTargets[I]), KeepName) = 0)", Whole("DropAside"));
    }

    [Fact]
    public void AnUninstallerFileInUseSendsTheUninstallerToAFolderOfItsOwn()
    {
        // Inno Setup writes the uninstaller (unins000.exe, .dat, .msg) itself, in place, with no way to leave it for a
        // restart: another program with one of the install folder's open would stop it, and an earlier uninstall's delete
        // of them at the next restart would delete the new ones. So when one is in use, or the setup finishes at the next
        // restart, the uninstaller goes in a folder of this run's own (UninstallFolder\<RunStamp>), which Apps and
        // Features then starts, and nothing stops the setup. The uninstaller that is no longer the one in use goes once the
        // files are in place, and is never left for the restart.
        Assert.Matches(@"(?m)^UninstallFilesDir=\{code:UninstallDir\}\r?$", Section("Setup"));
        Assert.Equal("uninstall", Constant("UninstallFolder"));
        Assert.Equal(OneLine("""
            function UninstallDir(Param: String): String;
            begin
              if UninstallerDir = '' then
              begin
                UninstallerDir := ExpandConstant('{app}');
                if FinishAtRestart or HeldUninstaller then
                  UninstallerDir := UninstallerDir + '\' + UninstallFolder + '\' + RunStamp;
                Log('Setup writes its uninstaller in ' + UninstallerDir + '.');
              end;
              Result := UninstallerDir;
            end;
            """), Whole("UninstallDir"));
        string held = Whole("FindHeld");
        Assert.Contains("Own := (Rel = '') and (CompareText(Copy(Name, 1, 5), 'unins') = 0);", held);
        Assert.Contains("else if Own then begin if not IsUninstaller and not CanReplace(Path) then begin HeldUninstaller := True;", held);
        // An uninstaller file in use is not one of the program's: it sends no setup to the restart and stops none.
        Assert.Contains("else if Own then begin if not IsUninstaller and not CanReplace(Path) then begin HeldUninstaller := True; if Logged then " +
                        "Log('In use, so Setup writes its uninstaller in a folder of its own: ' + Path + HoldersOf(Path)); end; end else if not CanReplace(Path) then", held);
        Assert.DoesNotContain("HeldUninstaller then begin Result", Code());
        string sweep = Whole("SweepUninstallers");
        // The install folder's own uninstaller files go when this run's is elsewhere; when this run's is there too, every
        // one but this run's (an earlier one a sweep could not delete makes Inno Setup number the next unins001).
        Assert.Contains("Mine := ChangeFileExt(ExtractFileName(ExpandConstant('{uninstallexe}')), ''); if FindFirst(App + '\\unins*', Find) then", sweep);
        Assert.Contains("if ((Find.Attributes and AttrFolder) = 0) and ((CompareText(UninstallerDir, App) <> 0) or (CompareText(ChangeFileExt(Find.Name, ''), Mine) <> 0)) then " +
                        "if DeleteFile(Path) then", sweep);
        Assert.Contains("(CompareText(Path, UninstallerDir) <> 0) then if DelTree(Path, True, True, True) then", sweep);
        Assert.DoesNotContain("RestartReplace", sweep);
        Assert.EndsWith("SweepUninstallers(App); end; end;", Whole("CurStepChanged"));
        Assert.StartsWith("procedure SweepUninstallers(const App: String); var Find: TFindRec; Path, Mine: String; begin if UninstallerDir = '' then Exit;", Whole("SweepUninstallers"));
        Assert.EndsWith("RemoveDir(App + '\\' + UninstallFolder); end;", Whole("SweepUninstallers")); // only once it is empty
        // The uninstaller has its own files open itself (Inno Setup deletes them), so it does not count them, wherever they are.
        Assert.Contains("if not IsUninstaller and not CanReplace(Path) then", held);
    }

    [Fact]
    public void TheUninstallerRemovesWhatIsStillInUseAtTheNextRestart()
    {
        // After Inno Setup removed what the setup installed, what is left of the install folder goes too: all it can
        // now (DelTree, which never follows a link), the rest at the next restart, each folder once empty and the
        // folder itself last. Only PoolWatch's own folder in Program Files, and not when IsLink, looking at it right
        // before, finds it to be a link.
        string rest = Body("RemoveRest");
        Assert.Contains("if (CompareText(App, ExpandConstant('{autopf}\\PoolWatch')) = 0) and DirExists(App) and not IsLink(App) then begin " +
                        "DelTree(App, True, True, True); if DirExists(App + '\\' + StageFolder) then KeepOutAtRestart(App); " +
                        "if DirExists(App) then DeleteAtRestart(App, ''); end;", rest);
        Assert.Contains("Link := ExpandConstant('{autoprograms}\\PoolWatch.lnk'); if FileExists(Link) then if not DeleteFile(Link) then DeleteOneAtRestart(Link, Link, HoldersOf(Link));", rest);
        string delete = Body("DeleteAtRestart");
        Assert.Contains("if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then DeleteAtRestart(Dir + '\\' + Name, Rel + Name + '\\') " +
                        "else DeleteOneAtRestart(Dir + '\\' + Name, Rel + Name, HoldersOf(Dir + '\\' + Name));", delete);
        Assert.EndsWith("DeleteOneAtRestart(Dir, '', ''); end;", Whole("DeleteAtRestart"));
        // Each file is named with what has it open, in the notice and in the log.
        Assert.Contains("if Shown <> '' then AddShown(RestartList, RestartCount, Shown + Holders); Log('Windows deletes it at its next restart: ' + Path + Holders);",
            Whole("DeleteOneAtRestart"));
        // A file an earlier setup set aside for the restart, still in use now, would be moved into the folder at that
        // restart: the place it goes is made now, so that it is deleted after the move.
        Assert.Contains("Target := App + '\\' + Rel + Name; if not FileExists(Target) then begin ForceDirectories(ExtractFileDir(Target)); if SaveStringToFile(Target, '', False) then",
            Body("KeepOut"));
        int keep = rest.IndexOf("KeepOutAtRestart(App);", StringComparison.Ordinal);
        int give = rest.IndexOf("DeleteAtRestart(App, '');", StringComparison.Ordinal);
        Assert.True(keep > 0 && give > keep, rest);
        // When that place cannot be made, the file may stay after the restart (if that setup's move of it is still to
        // happen, which the uninstaller cannot tell: the setup undoes such a move when it takes its new files back,
        // Unstage): it is counted, and logged so.
        Assert.Contains("if SaveStringToFile(Target, '', False) then Log('Still in use: ' + Dir + '\\' + Name + ', which an earlier setup gave Windows to move to ' + " +
                        "Target + ' at the next restart. ' + Target + ' is made now, so that Windows deletes it after that move.') " +
                        "else begin RestartStays := RestartStays + 1; Log('May stay after the next restart: ' + Target + ', if an earlier setup gave Windows ' +", Body("KeepOut"));
        Assert.Contains("the moves of any new file that could not be deleted are undone (Unstage)", OneLine(CodeSection()));
        // Each thing Windows is given is counted, a folder (which no list names) as well as a file; RemoveRest starts each
        // count at 0.
        Assert.Contains("try RestartReplace(Path, ''); RestartGiven := RestartGiven + 1; if Shown <> '' then", Body("DeleteOneAtRestart"));
        Assert.Contains("RestartList := ''; RestartCount := 0; RestartGiven := 0; RestartStays := 0; RestartFailed := 0;", Body("RemoveRest"));
        // What goes at the restart is said: in the log, and in a box when someone is there, whenever Windows was given
        // anything, a folder alone included, could not be given something, or a file may stay (the block's condition, held
        // by ASilentUninstallShowsNoBoxAfterTheCleanup), with what Windows could not be given and what may stay; the
        // install folder is said to go only when nothing stays and Windows was given a folder (the install folder's are
        // the only folders it is given, the install folder itself last); Inno Setup's own last words, when its own removal
        // left a file, say the same.
        string text = Whole("RestartText");
        Assert.Contains("'The uninstaller does not restart Windows: restart it when it suits you.'", text);
        Assert.Contains("FolderGoes := (RestartFailed + RestartStays = 0) and (RestartGiven > RestartCount);", text);
        Assert.Contains("if (RestartCount > 0) and FolderGoes then Result := Result + ' Windows deletes them, and the folder ' + App + ', when it next restarts:'", text);
        Assert.Contains("else if FolderGoes then Result := Result + ' Windows deletes the folder ' + App + ', with the folders left in it, when it next restarts.';", text);
        Assert.Contains("if RestartFailed > 0 then Result := Result + #13#10#13#10 + Format('The uninstaller could not give Windows %d of PoolWatch''s files or ' + " +
                        "'folders to delete at the restart.', [RestartFailed]);", text);
        Assert.Contains("if RestartStays > 0 then Result := Result + #13#10#13#10 + Format('An earlier update of PoolWatch may have Windows move %d of its files ' + " +
                        "'into %s at the restart, where they would stay after it:", text);
        Assert.Contains("if RestartFailed + RestartStays > 0 then Result := Result + ' After the restart, delete what is left of ' + App +", text);
        Assert.Equal(2, Regex.Matches(text, @"' Windows deletes (them, and )?the folder ' \+ App").Count); // only those two say it
        // The log's note says the uninstall finishes at the restart only when nothing was refused and nothing may stay.
        string note = Whole("RestartLogNote");
        Assert.Contains("if RestartFailed > 0 then Result := 'This uninstall does not finish at the next restart of Windows:", note);
        Assert.Contains("else if RestartStays > 0 then Result := 'This uninstall may not finish at the next restart of Windows:", note);
        Assert.Contains("else Result := 'This uninstall finishes at the next restart of Windows, which deletes what is named above.';", note);
        Assert.Single(Regex.Matches(note, "finishes at the next restart"));
        Assert.Matches(@"(?m)^UninstalledMost=PoolWatch was removed from your computer\.%n%nWhat was still in use is deleted when Windows next restarts", Section("Messages"));
    }

    [Fact]
    public void ACleanupThatFailedForAnyReasonRemovesNoFile()
    {
        // Only 0 (all put back, or kept as asked), 8 (what could not be put back stays listed in the data folder for a
        // later PoolWatch) and 12 (either of those on the folder PoolWatch kept its data in while it could not use the usual
        // one) let the uninstaller remove files. Any other code, one it does not know included, is a failure:
        // the cleanup could not be started (-1), its data folder was not safe (6), PoolWatch still runs (7), it could not
        // open its private names (9), or it broke (1). A cleanup that broke may have put back part of the changes, which
        // the uninstaller cannot tell, so it says that, never that nothing was put back.
        Assert.Contains("Result := (Code = 0) or (Code = ExitNotAllPutBack) or (Code = ExitOtherDataFolder);", Body("CleanupDone"));
        Assert.Equal(Engine.ExitCodes.OtherDataFolder, Number("ExitOtherDataFolder"));
        Assert.Equal((Engine.ExitCodes.DataNotSafe, Engine.ExitCodes.StillRunning, Engine.ExitCodes.NotAllPutBack, Engine.ExitCodes.NamesNotSafe),
            (Number("ExitDataNotSafe"), Number("ExitStillRunning"), Number("ExitNotAllPutBack"), Number("ExitNamesNotSafe")));
        Assert.Contains("else Result := Format('PoolWatch''s cleanup failed (error %d). It may have put back some of its changes, and the ' + " +
                        "'uninstaller cannot tell which, so it has removed nothing. Click Retry to try again. If this keeps happening, run ' + " +
                        "'PoolWatch''s setup again to repair it, then uninstall it.', [Code]);", Body("NotRemovedText"));
        Assert.DoesNotContain("could not put back its changes", Script());
        // The loop: while the cleanup failed, a silent uninstall ends there with nothing removed; one started by hand says
        // why and offers Retry, which runs the cleanup again. Nothing goes past it before the cleanup did what it was asked.
        string code = Code();
        Assert.Matches(@"repeat while not CleanupDone\(Code\) do begin Log\(NotRemovedText\(Code\)\); if UninstallSilent then Abort; " +
                       @"if MsgBox\(NotRemovedText\(Code\), mbError, MB_RETRYCANCEL\) <> IDRETRY then Abort; Code := RunCleanup\(KeepChanges, Summarized\); end; " +
                       @"Summarized := True;",
            code[code.IndexOf("usAppMutexCheck:", StringComparison.Ordinal)..]);
        // The comment above the cleanup says why (the code read without comments has none of it).
        string written = OneLine(CodeSection());
        Assert.Contains("may have put back only part of the changes, and the uninstaller cannot tell which", written[written.IndexOf("usAppMutexCheck:", StringComparison.Ordinal)..]);
        // RunCleanup maps a start failure to -1 (its own code), which CleanupDone then treats as a failure that removes
        // nothing. Held whole so that losing the mapping (a CreateProcess error read as the cleanup's exit code) fails here.
        Assert.Equal(OneLine("""
            function RunCleanup(KeepChanges, Append: Boolean): Integer;
            var
              Params, Launcher: String;
            begin
              Params := '--uninstall-cleanup';
              if KeepChanges then
                Params := Params + ' --keep-changes';
              if Append then
                Params := Params + ' --append-summary';
              Launcher := LauncherPath(ExpandConstant('{app}'));
              if Launcher = '' then
              begin
                Log('The install folder has no launcher of the same version as PoolWatch''s program, ' + ProgramPath(ExpandConstant('{app}')) +
                  ', so PoolWatch''s cleanup was not started.');
                Result := NotInPlace;
              end
              else if NameGoesBack(ExpandConstant('{app}'), PoolWatchName) or NameGoesBack(ExpandConstant('{app}'), SelfContainedName) then
              begin
                Log('One of the launcher''s names in the install folder goes back only at the next restart, so PoolWatch''s cleanup ' +
                  'was not started.');
                Result := NotInPlace;
              end
              else if Exec(Launcher, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
                Log(Format('PoolWatch --uninstall-cleanup: exit code %d.', [Result]))
              else
              begin
                Log(Format('PoolWatch could not be started for its cleanup (error %d).', [Result]));
                Result := -1;
              end;
            end;
            """), Whole("RunCleanup"));
    }

    /// <summary>
    /// The layout the setup installs: the install folder's PoolWatch.exe is the launcher, and PoolWatch itself is
    /// app\PoolWatch.exe below it, each named in [Files] (a build without one fails) and neither again by the folder's
    /// wildcard. The setup and the uninstaller start PoolWatch only through the launcher, which gives it a clean
    /// environment: the Start menu entry, "Start PoolWatch now", the stop before an upgrade and the uninstaller's two steps.
    /// A file with one of the launcher's names counts as the launcher only when PoolWatch itself is below it with the same
    /// product version, as PoolWatch's own LaunchGate.Judge decides (an older program can be left under the launcher's name
    /// where a zip was unpacked over a copy that was running). Without one, the setup sets the exit signal of a PoolWatch
    /// installed before the launcher and starts nothing, and the uninstaller starts nothing. The setup has no logon task
    /// step: a task made before 1.0 named the install folder's PoolWatch.exe, which is the launcher now.
    /// </summary>
    [Fact]
    public void TheSetupStartsPoolWatchOnlyThroughALauncherOfItsProgramsVersion()
    {
        string script = Script();
        string code = Code();
        // The layout, decided in one place each, and the names it shares with PoolWatch.
        Assert.Equal(Engine.LaunchGate.ProgramFileName, Constant("PoolWatchName"));
        Assert.Equal(Engine.LaunchGate.AppFolderName, Constant("AppFolder"));
        Assert.Equal(Engine.LaunchGate.LauncherFileNames[1], Constant("SelfContainedName"));
        Assert.Contains("function SecondLauncherPath(const App: String): String; begin Result := App + '\\' + SelfContainedName; end;", code);
        Assert.Contains("function ProgramPath(const App: String): String; begin Result := App + '\\' + AppFolder + '\\' + PoolWatchName; end;", code);
        Assert.Contains("function HasLauncher(const App: String): Boolean; begin Result := FileExists(ProgramPath(App)); end;", code);
        // By name only, for the list of processes and the wait.
        Assert.Contains("function LauncherFile(const App: String): String; begin Result := App + '\\' + PoolWatchName; " +
                        "if not FileExists(Result) and FileExists(SecondLauncherPath(App)) then Result := SecondLauncherPath(App); end;", code);
        // To start: LaunchGate.Judge's rule, the program below it and the same product version (never an empty one), the
        // usual name first, as LaunchGate.LauncherOf picks.
        Assert.Equal(OneLine("""
            function IsLauncherOf(const Path, App: String): Boolean;
            var
              Version: String;
            begin
              Result := False;
              if not FileExists(Path) or not HasLauncher(App) then
                Exit;
              Version := ProductVersionOf(Path);
              Result := (Version <> '') and (Version = ProductVersionOf(ProgramPath(App)));
            end;
            """), Whole("IsLauncherOf"));
        Assert.Equal(OneLine("""
            function LauncherPath(const App: String): String;
            begin
              Result := '';
              if IsLauncherOf(App + '\' + PoolWatchName, App) then
                Result := App + '\' + PoolWatchName
              else if IsLauncherOf(SecondLauncherPath(App), App) then
                Result := SecondLauncherPath(App);
            end;
            """), Whole("LauncherPath"));
        Assert.Equal(Engine.LaunchGate.LauncherFileNames[0], Constant("PoolWatchName"));
        // Every Exec of PoolWatch is the launcher's, each from LauncherPath, and none without one.
        Assert.Equal(3, Regex.Matches(code, Regex.Escape("Exec(Launcher, ")).Count);
        Assert.Equal(3, Regex.Matches(code, Regex.Escape("Launcher := LauncherPath(")).Count);
        Assert.Equal(3, Regex.Matches(code, @"\bExec\(").Count);
        Assert.DoesNotMatch(@"Exec\((ExpandConstant\('\{app\}|Exe,|ProgramPath\(|LauncherFile\()", code);
        Assert.Contains("Launcher := LauncherPath(App); if Launcher = '' then StopBeforeLauncher else if Exec(Launcher, '--stop-running', ", Body("PrepareToInstall"));
        Assert.Contains("Launcher := LauncherPath(ExpandConstant('{app}')); if Launcher = '' then begin Log(", Body("RunCleanup"));
        Assert.Contains("Result := NotInPlace; end else if Exec(Launcher, Params,", Body("RunCleanup"));
        Assert.Contains("Launcher := LauncherPath(ExpandConstant('{app}')); if (Launcher = '') or not Exec(Launcher, '--changes-dump \"' + ListFile + '\"'", Body("ReadChangeList"));
        Assert.Contains("else if Code = NotInPlace then begin if RestartWorkPending(ExpandConstant('{app}')) then Result := 'An update of PoolWatch, a setup of it " +
                        "that did not finish, or an earlier uninstall finishes when Windows next restarts", Joined(Body("NotRemovedText")));
        Assert.Contains("is not there, or the launcher beside it is of another version, so PoolWatch put nothing back and the uninstaller has removed nothing. " +
                        "Run PoolWatch's setup again to repair it, then uninstall it.", Joined(Body("NotRemovedText")));
        Assert.False(Whole("CleanupDone").Contains("NotInPlace", StringComparison.Ordinal)); // a failure: nothing is removed
        Assert.Equal(-2, int.Parse(Regex.Match(script, @"(?m)^\s*NotInPlace = (-\d+);").Groups[1].Value));

        // The shortcut and the [Run] entry, each a whole line of its own (not a comment): the launcher.
        string[] lines = script.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        const string startLine = @"Filename: ""{app}\PoolWatch.exe""; Description: ""Start PoolWatch now""; Flags: postinstall nowait skipifsilent runascurrentuser; Check: StartsNow";
        Assert.Contains(@"Name: ""{autoprograms}\PoolWatch""; Filename: ""{app}\PoolWatch.exe""; AppUserModelID: ""PoolWatch.Monitor""", lines);
        int run = Array.IndexOf(lines, "[Run]"), start = Array.IndexOf(lines, startLine);
        Assert.True(run >= 0 && run < start && !lines[(run + 1)..start].Any(l => l.StartsWith('[')), $"[Run] at line {run}, the start at {start}");
        Assert.DoesNotMatch(@"(?m)(^|; )Filename: ""\{app\}\\app\\", script); // never PoolWatch itself
        Assert.DoesNotContain("--update-logon-task", script);
        Assert.DoesNotContain("schtasks", script);

        // The files: the launcher and PoolWatch itself by name, the rest by the wildcard without them.
        Assert.Contains(@"Source: ""{#PublishDir}\PoolWatch.exe""; DestDir: ""{code:ProgramDir}""; Flags: ignoreversion; BeforeInstall: Shipping", lines);
        Assert.Contains(@"Source: ""{#PublishDir}\PoolWatch.exe""; DestDir: ""{code:ProgramDir}""; DestName: ""PoolWatch-selfcontained.exe""; Flags: ignoreversion; Check: SelfContainedNameThere; BeforeInstall: Shipping", lines);
        Assert.Contains(@"Source: ""{#PublishDir}\app\PoolWatch.exe""; DestDir: ""{code:ProgramDir}\app""; Flags: ignoreversion; BeforeInstall: Shipping", lines);
        Assert.Contains(@"Excludes: ""\PoolWatch.exe,\PoolWatch-selfcontained.exe,\app\PoolWatch.exe,\chatgpt-no-shader-cache.ps1,\AGENTS.md,\LICENSE,\THIRD-PARTY-NOTICES.md,\poolwatch.png""", script);
        Assert.Contains("CloseApplicationsFilter=*.closes-no-program\n", script.Replace("\r\n", "\n")); // TheSetupClosesNoProgram
        Assert.DoesNotMatch(@"(?m)^\[InstallDelete\]", script);

        // The stop without a launcher of the program's version: the global exit signal of a PoolWatch installed before
        // the launcher, set here, and no PoolWatch started.
        Assert.Equal(@"Global\PoolWatch-Exit", Constant("OldExitEvent"));
        Assert.Equal(OneLine("""
            procedure StopBeforeLauncher;
            begin
              SetOldExit(True);
              StoppingOld := True;
            end;
            """), Whole("StopBeforeLauncher"));
        Assert.Equal(OneLine("""
            procedure SetOldExit(Logged: Boolean);
            var
              Event: THandle;
            begin
              Event := OpenEvent(EVENT_MODIFY_STATE, False, OldExitEvent);
              if Event = 0 then
              begin
                if Logged then
                  Log(Format('No PoolWatch made before the launcher is listening for its exit signal (error %d): the files decide.', [DLLGetLastError]));
              end
              else
              begin
                if SetEvent(Event) then
                begin
                  if Logged then
                    Log('The installed PoolWatch, made before the launcher, was asked to exit through its exit signal.');
                end
                else if Logged then
                  Log(Format('The exit signal of the installed PoolWatch could not be set (error %d): the files decide.', [DLLGetLastError]));
                CloseHandle(Event);
              end;
            end;
            """), Whole("SetOldExit"));
        Assert.Contains("function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle; external 'OpenEventW@kernel32.dll stdcall';", code);
        Assert.Contains("EVENT_MODIFY_STATE = $0002;", script);
    }

    /// <summary>A text of the script's with each of its string literals joined to the next ('a' + 'b' read as 'ab'), and each '' read as ', as Windows shows it.</summary>
    private static string Joined(string text) => Regex.Replace(text, @"'\s*\+\s*'", "").Replace("''", "'");

    /// <summary>
    /// The script's ProductVersionOf, line for line, over the block GetFileVersionInfoW copies (as the UTF-16 characters
    /// the script's String holds; its CharAt gives #0 outside the block, and Pos is Pascal's, from 1, 0 when not found).
    /// The first test holds the script's text, so the two change together.
    /// </summary>
    private static string ScriptProductVersionOf(string data)
    {
        char CharAt(int i) => i >= 1 && i <= data.Length ? data[i - 1] : '\0';
        int p = data.IndexOf("ProductVersion\0", StringComparison.Ordinal) + 1;
        if (p < 4 || CharAt(p - 2) == '\0') return "";
        int v = p + "ProductVersion".Length + 1;
        if (v % 2 == 0) v++;
        var result = new StringBuilder();
        while (CharAt(v) != '\0') { result.Append(CharAt(v)); v++; }
        return result.ToString();
    }

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileVersionInfoSizeW(string filename, out uint handle);

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileVersionInfoW(string filename, uint handle, uint size, [Out] byte[] data);

    /// <summary>The block GetFileVersionInfoW copies for <paramref name="path"/>, as the characters the script's String holds ((Size + 1) div 2 of them).</summary>
    private static string VersionBlock(string path)
    {
        uint size = GetFileVersionInfoSizeW(path, out _);
        Assert.True(size > 0, path + " has no version resource");
        var bytes = new byte[(size + 1) / 2 * 2];
        Assert.True(GetFileVersionInfoW(path, 0, size, bytes), path);
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    [Fact]
    public void TheScriptReadsAProgramsProductVersionAsWindowsDoes()
    {
        // The setup and the uninstaller tell the launcher from an older program left under its name by the product version
        // (IsLauncherOf), which they read from the version resource themselves (no pointer comes back to a script). The
        // reader, copied here line for line, gives what .NET's FileVersionInfo gives (the reader PoolWatch's own
        // LaunchGate.Judge uses) for PoolWatch's own programs and for Windows' own files.
        var files = new[] { "PoolWatch.dll", "PoolWatch.exe", "PoolWatch.Launcher.dll", "PoolWatch.Core.dll", "PoolWatch.AppTests.dll" }
            .Select(f => Path.Combine(AppContext.BaseDirectory, f)).Where(File.Exists)
            .Concat([Path.Combine(Environment.SystemDirectory, "kernel32.dll"), Path.Combine(Environment.SystemDirectory, "version.dll")]).ToList();
        Assert.True(files.Count >= 5, string.Join(", ", files));
        foreach (var file in files)
        {
            string expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).ProductVersion ?? "";
            Assert.False(string.IsNullOrEmpty(expected), file);
            Assert.Equal(expected, ScriptProductVersionOf(VersionBlock(file)));
        }
        // PoolWatch's launcher and program of one build have one product version, its build's version and commit.
        Assert.Equal(Engine.LaunchGate.ProductVersionOf(Path.Combine(AppContext.BaseDirectory, "PoolWatch.dll")),
            ScriptProductVersionOf(VersionBlock(Path.Combine(AppContext.BaseDirectory, "PoolWatch.Launcher.dll"))));
        // No block, or a key without a value: no version, so no launcher.
        Assert.Equal("", ScriptProductVersionOf(""));
        Assert.Equal("", ScriptProductVersionOf("@\u0000\u0001ProductVersion\0\0\0"));
        Assert.Contains("SetLength(Data, (Size + 1) div 2); if not GetFileVersionInfoW(Path, 0, Size, Data) then Exit;", Whole("ProductVersionOf"));
        // The script's own lines that the copy above follows: where the key is found, the check that its value is not
        // empty, where the value starts (on a 32-bit boundary: an odd character position), and how far it is read.
        Assert.Contains("P := Pos('ProductVersion' + #0, Data); if (P < 4) or (Ord(CharAt(Data, P - 2)) = 0) then Exit; " +
                        "V := P + Length('ProductVersion') + 1; if (V mod 2) = 0 then V := V + 1; while CharAt(Data, V) <> #0 do begin " +
                        "Result := Result + CharAt(Data, V); V := V + 1; end;", Whole("ProductVersionOf"));
        Assert.Contains("function GetFileVersionInfoW(Filename: String; Handle, Size: DWORD; Data: String): BOOL; external 'GetFileVersionInfoW@version.dll stdcall delayload';", Code());
    }

    /// <summary>One RM_PROCESS_INFO as Restart Manager writes it (restartmanager.h).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public int ProcessId;
        public uint StartTimeLow, StartTimeHigh;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint processCount, IntPtr processes, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] byte[] infos, out uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    /// <summary>
    /// The script's reading of RmGetList's list (HoldersOf), line for line: each entry RmInfoChars characters, its process
    /// ID in the first two, its program's name from the seventh up to its #0 (at most 256), each control character of the
    /// name and Unicode's line and paragraph separators put as a space. The first test holds the script's text, so the
    /// two change together.
    /// </summary>
    private static List<(int Id, string Name)> ScriptHolders(string infos, int count)
    {
        int chars = Number("RmInfoChars");
        char CharAt(int i) => i >= 1 && i <= infos.Length ? infos[i - 1] : '\0';
        var holders = new List<(int, string)>();
        for (int i = 0; i < count; i++)
        {
            int b = i * chars + 1;
            int id = CharAt(b) + CharAt(b + 1) * 65536;
            var name = new StringBuilder();
            for (int c = b + 6; c < b + 6 + 256 && CharAt(c) != '\0'; c++)
            {
                int k = CharAt(c);
                name.Append(k < 32 || (k >= 127 && k < 160) || k == 8232 || k == 8233 ? ' ' : CharAt(c));
            }
            holders.Add((id, name.ToString()));
        }
        return holders;
    }

    [Fact]
    public void TheScriptReadsRestartManagersListAsWindowsWritesIt()
    {
        // The setup and the uninstaller name what has a file in use with Restart Manager, reading its list from a buffer
        // of their own. Each entry is one RM_PROCESS_INFO (668 bytes, so 334 characters), as Windows writes it; the
        // script's reader, copied here, gives each entry's process ID and program name as the structure holds them, for a
        // file this test has open itself.
        Assert.Equal(Marshal.SizeOf<RmProcessInfo>(), Number("RmInfoChars") * 2);
        Assert.Equal(33, Number("RmKeyChars")); // CCH_RM_SESSION_KEY + 1
        Assert.Equal(234, Number("ErrorMoreData")); // ERROR_MORE_DATA
        string file = Path.Combine(Path.GetTempPath(), "pw-held-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "held");
        try
        {
            using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(0, RmStartSession(out uint session, 0, new StringBuilder(Number("RmKeyChars"))));
            try
            {
                Assert.Equal(0, RmRegisterResources(session, 1, [file], 0, IntPtr.Zero, 0, IntPtr.Zero));
                uint count = (uint)Number("RmMaxRead");
                var buffer = new byte[count * Number("RmInfoChars") * 2];
                Assert.Equal(0, RmGetList(session, out _, ref count, buffer, out _));
                Assert.True(count >= 1, "Restart Manager named no process that has the file open");
                var script = ScriptHolders(new string(MemoryMarshal.Cast<byte, char>(buffer)), (int)count);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<RmProcessInfo>(handle.AddrOfPinnedObject() + i * Marshal.SizeOf<RmProcessInfo>());
                        Assert.Equal((info.ProcessId, info.AppName), script[i]);
                    }
                }
                finally { handle.Free(); }
                Assert.Contains(script, h => h.Id == Environment.ProcessId && h.Name.Length > 0);
            }
            finally { RmEndSession(session); }
        }
        finally { File.Delete(file); }
        // The text it gives, after the file's name: each program, its process ID and its account.
        Assert.Contains("Result := Result + Format('%s, process %d, run by %s', [Name, Id, AccountOf(Id)]); end; Result := ' (in use by ' + Result + ')';", Whole("HoldersOf"));
        // The script's own lines that ScriptHolders follows: each entry's start, its process ID in its first two characters,
        // and its program's name from its seventh, at most 256 characters, up to its #0, each control character and line
        // or paragraph separator put as a space.
        Assert.Contains("for I := 0 to N - 1 do begin Base := I * RmInfoChars + 1; Id := Ord(CharAt(Infos, Base)) + Ord(CharAt(Infos, Base + 1)) * 65536; " +
                        "Name := ''; C := Base + 6; while (C < Base + 6 + 256) and (CharAt(Infos, C) <> #0) do begin K := Ord(CharAt(Infos, C)); " +
                        "if (K < 32) or ((K >= 127) and (K < 160)) or (K = 8232) or (K = 8233) then Name := Name + ' ' else Name := Name + CharAt(Infos, C); C := C + 1; end;",
            Whole("HoldersOf"));
        // A program chooses its own name, and the log and the boxes put it after a file's name: each control character of
        // it (a line feed, a carriage return, a tab, DEL, a C1 control such as NEL) and Unicode's line and paragraph
        // separators, some of which start a new line there, is read as a space, so that the name stays on its line and no
        // line of the log starts with words of it. Every other character is read as it is.
        int chars = Number("RmInfoChars");
        char[] made = new char[2 * chars];
        void Entry(int at, int id, string name) { made[at] = (char)(id & 0xFFFF); made[at + 1] = (char)(id >> 16); name.CopyTo(0, made, at + 6, name.Length); }
        Entry(0, 0x12345, "Tool\r\nWindows deletes it at its next restart: C:\\x\u2028y\u0085z\tw\u007f!\u2029\u00e9");
        Entry(chars, 7, "Plain name");
        Assert.Equal([(0x12345, "Tool  Windows deletes it at its next restart: C:\\x y z w ! \u00e9"), (7, "Plain name")], ScriptHolders(new string(made), 2));
    }

    /// <summary>
    /// An upgrade from a layout before the launcher (1.0.0-beta.1: PoolWatch itself as the install folder's PoolWatch.exe,
    /// its native DLLs in amd64\ beside it) leaves none of that layout's files: once the new files are in place each is
    /// deleted, or, when the setup finishes at the next restart, given to Windows to delete after the moves (and not at
    /// all when Windows could not be given the moves, so the version that was there stays whole). So is the launcher's
    /// name in builds before this layout. The old PoolWatch.exe itself is replaced by the launcher as any file is (set
    /// aside first: SetAllAside), and so is a PoolWatch-selfcontained.exe there (the next test).
    /// </summary>
    [Fact]
    public void AnUpgradeLeavesNoFileOfTheLayoutBeforeTheLauncher()
    {
        Assert.Equal(OneLine("""
            function OldLayoutFile(I: Integer): String;
            begin
              case I of
                0: Result := 'amd64\KernelTraceControl.dll';
                1: Result := 'amd64\msdia140.dll';
                2: Result := 'PoolWatchLauncher.exe';
              else
                Result := '';
              end;
            end;
            """), Whole("OldLayoutFile"));
        string remove = Body("RemoveOldLayout");
        Assert.Contains("if not AtRestart and DeleteFile(Path) then Log('Deleted ' + Path", remove);
        Assert.Contains("else try RestartReplace(Path, ''); Log('Windows deletes ' + Path", remove);
        Assert.Contains("Path := App + '\\amd64'; if DirExists(Path) then begin if not AtRestart and RemoveDir(Path) then", remove);
        Assert.Contains("if FinishAtRestart then begin SwapAtRestart; Log(FinishHeading + '. ' + FinishText + FinishFiles); if NotScheduled = 0 then RemoveOldLayout(App, True); end " +
                        "else begin DropAside; RemoveOldLayout(App, False);", Body("CurStepChanged"));
        // What beta.1's setup installed besides PoolWatch.exe and the files that stay where they were (from its release
        // build's log): exactly the two native DLLs, now below app\.
        foreach (var dll in new[] { "KernelTraceControl.dll", "msdia140.dll" })
            Assert.Contains($"'amd64\\{dll}'", Body("OldLayoutFile"));
        // A setup that finishes at the restart after one that did not finish (or that could not put the old files back
        // itself): such a file is then set aside under <file>.<run>.old and goes back at the restart, before this run's
        // deletes, so a look at its own name finds nothing. It is deleted now under the name it was set aside under (its
        // move then finds nothing), or, when it is in use, its own name goes after that move.
        Assert.Contains("I := 0; while OldLayoutFile(I) <> '' do begin Path := App + '\\' + OldLayoutFile(I); if FileExists(Path) then begin", remove);
        Assert.Contains("end else if AtRestart then DropOldLayoutAside(Path); I := I + 1; end;", remove);
        string aside = Whole("DropOldLayoutAside");
        Assert.Contains("if FindFirst(Path + '.*.old', Find) then try repeat Aside := ExtractFileDir(Path) + '\\' + Find.Name; " +
                        "if ((Find.Attributes and AttrFolder) = 0) and MovesAtRestart(Aside) then begin if DeleteFile(Aside) then Log(", aside);
        Assert.Contains("else try RestartReplace(Path, ''); Log('Windows deletes ' + Path +", aside);
        Assert.Contains("Aside := Path + '.' + RunStamp + '.old';", Body("SetAsideIn")); // the name it was set aside under
    }

    [Fact]
    public void AFailedSetupLeavesNoFolderOnlyItsNewFilesWereIn()
    {
        // Inno Setup's rollback leaves a folder it made while a new file it could not delete was in it (app\ on an upgrade
        // from 1.0.0-beta.1, which has no app folder), and the uninstaller of that version would not delete it. After the
        // take-back and the put-back, each folder a file of this version is in goes, deepest first, when it is empty; while
        // a new file stays until the restart, Windows is given it to delete then, after the file's delete and the old files'
        // moves back, which come first in its list, and it deletes a folder then only while it is empty: one an old file
        // goes back into stays. Not for a setup that put its files in its own folder.
        string deinit = Whole("DeinitializeSetup");
        Assert.Contains("PutAllBack(Later); if not FinishAtRestart then DropNewFolders(App, Later);", deinit);
        string drop = Whole("DropNewFolders");
        Assert.Contains("for I := 0 to GetArrayLength(Shipped) - 1 do begin Dir := ExtractFileDir(Shipped[I]); " +
                        "while (Length(Dir) > Length(App) + 1) and (CompareText(Copy(Dir, 1, Length(App) + 1), App + '\\') = 0) do begin", drop);
        Assert.Contains("Dir := ExtractFileDir(Dir); end; end;", drop);
        Assert.Contains("Deepest := 0; for J := 1 to GetArrayLength(Dirs) - 1 do if Length(Dirs[J]) > Length(Dirs[Deepest]) then Deepest := J; " +
                        "Dir := Dirs[Deepest]; Dirs[Deepest] := '';", drop);
        Assert.Contains("if DirExists(Dir) then begin if RemoveDir(Dir) then Log(", drop);
        Assert.Contains("else if Later then try RestartReplace(Dir, '');", drop);
    }

    [Fact]
    public void AFailedSetupSaysWhatWindowsWouldNotTakeForTheRestartAndNamesEachFileInUse()
    {
        // MoveFileEx's restart-time rename fails when it cannot write its list in the registry (Microsoft's MoveFileExW
        // documentation). An old file Windows will not take to put back then stays under the name it was set aside under,
        // and a new file it will not take to delete then stays after the restart too, so the restart does not put the
        // version that was there back whole: each such refusal is counted, and the setup then says what happens to any file
        // of either kind (one count holds both, so neither is said to have happened), that PoolWatch may not start after
        // the restart until the setup has run again, and to run it again before uninstalling; when a new file stays under
        // its own name, it also says the files that start the version that was there stay set aside, and when that file is
        // at one of the launcher's names, that every file of that version does (PutAllBack). The box names the first
        // MaxShown (ten) files in use then, each with what has it open, and how many more (MoreLine); the log names every
        // one. The docs say "the first ten", so MaxShown is held here.
        Assert.Contains("try RestartReplace(Aside, Target); Log('Windows puts ' + Target + ' back at its next restart, from ' + Aside + '.'); except Refused := Refused + 1;",
            Whole("MoveBackAtRestart"));
        Assert.Contains("try RestartReplace(Aside, Target); Log('Could not put back ' + Target + ' now, as ' + Aside + ' is in use' + HoldersOf(Aside) + " +
                        "': Windows moves it back at its next restart.'); except Refused := Refused + 1;", Whole("PutBack"));
        string take = Whole("TakeBack");
        Assert.Contains("try RestartReplace(Path, ''); Log('Windows deletes ' + Path + ' at its next restart, before it puts the old files back.'); except Refused := Refused + 1;", take);
        Assert.Contains("try RestartReplace(Moved, ''); Log('Windows deletes ' + Moved + ' at its next restart.'); except Refused := Refused + 1;", take);
        Assert.Equal(4, Regex.Matches(Code(), @"\bRefused := Refused \+ 1;").Count);
        string deinit = Whole("DeinitializeSetup");
        Assert.Contains("'every file of the version you had back now'; if LauncherLeftNew(App) then Text := Text + ', and a new file that starts PoolWatch stays " +
                        "after the next restart too, as Windows would not ' + 'take it to delete then: so that nothing starts PoolWatch from old and new files mixed, " +
                        "every file of the ' + 'version you had stays under the name Setup set it aside under (Setup''s log names each), and PoolWatch may ' + " +
                        "'not start after that restart until the setup has run again' else begin Text := Text + ': Windows puts back the rest when it next restarts'; " +
                        "if Refused > 0 then Text := Text + ', but for what it would not take for that restart: any old file it would not take to put ' + " +
                        "'back stays under the name Setup set it aside under, and any new file it would not take to delete stays ' + 'after the restart " +
                        "(Setup''s log names each), so PoolWatch may not start after that restart until the ' + 'setup has run again'; if LeftNew <> '' then " +
                        "Text := Text + '. As a new file stays after the restart under its own name, the files that start the version ' + 'you had stay set aside " +
                        "then too, so that they start nothing from old and new files mixed'; end; Text := Text + '. Until that restart ' + UntilRestart(App) + '.';", deinit);
        Assert.Contains("if Refused > 0 then Text := Text + ' Run the setup again before you uninstall PoolWatch.'", deinit);
        // What the uninstaller would do comes whether or not Windows refused something (it is said before that advice).
        Assert.True(deinit.IndexOf("if not Held then Text := Text + ' Do not uninstall", StringComparison.Ordinal) <
                    deinit.IndexOf("if Refused > 0 then Text := Text + ' Run the setup again", StringComparison.Ordinal), deinit);
        Assert.Equal(10, Number("MaxShown"));
        Assert.Contains("Count := Count + 1; if Count <= MaxShown then List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown);", Body("AddShown"));
        Assert.Contains("if Count > MaxShown then Result := #13#10 + Format('And %d more.', [Count - MaxShown])", Body("MoreLine"));
        Assert.Contains("if Refused > 0 then Result := Result + ' Windows would not take every old file Setup could not put back now to put back at that ' + " +
                        "'restart either, so PoolWatch may not start after it until the setup has run again.';", Whole("FinishText"));
        // Each file in use, from a logged look after the put-back (HeldFiles logs each with what has it open).
        Assert.Contains("Text := Text + ' Setup does not restart Windows: restart it when it suits you.'; if HeldFiles(App, True) > 0 then Text := Text + " +
                        "' The files in use now, each with what has it open (Setup''s log, ' + ExpandConstant('{log}') + ', names every one):' + HeldList + MoreLine(HeldCount)", deinit);
        Assert.True(deinit.IndexOf("HeldFiles(App, True)", StringComparison.Ordinal) > deinit.IndexOf("PutAllBack(Later);", StringComparison.Ordinal), deinit);
    }

    /// <summary>
    /// Where a copy of the self-contained zip was unpacked into the install folder, its PoolWatch-selfcontained.exe (the
    /// launcher, or before 1.0 PoolWatch itself) may be what a logon task or a shortcut that copy made starts. The setup
    /// never deletes it: where a file of that name is there, [Files] puts the launcher there too, as it does at
    /// PoolWatch.exe, so that task or shortcut keeps starting PoolWatch through its launcher. And the setup's stop and its
    /// wait know the launcher by that name too: the stop runs through whichever of the two is the launcher (LauncherPath), and the
    /// wait counts a launcher that runs under either name (WaitForApp, RunningFromApp, FromApp, PoolWatchProcesses).
    /// </summary>
    [Fact]
    public void TheSelfContainedZipsLauncherNameKeepsStartingPoolWatchAfterTheSetup()
    {
        Assert.Contains("SecondNameWas := FileExists(SecondLauncherPath(ExpandConstant('{app}'))); end; Result := SecondNameWas; end;", Code());
        var files = Regex.Matches(Section("Files"), @"(?m)^Source: .*$").Select(m => m.Value.TrimEnd('\r')).ToList();
        int launcher = files.FindIndex(l => l.StartsWith(@"Source: ""{#PublishDir}\PoolWatch.exe""; DestDir: ""{code:ProgramDir}""; Flags:", StringComparison.Ordinal));
        Assert.True(launcher >= 0 && files[launcher + 1].Contains(@"DestName: ""PoolWatch-selfcontained.exe""; Flags: ignoreversion; Check: SelfContainedNameThere", StringComparison.Ordinal));
        Assert.Equal(Engine.LaunchGate.LauncherFileNames[1], Regex.Match(files[launcher + 1], @"DestName: ""([^""]+)""").Groups[1].Value);
        Assert.DoesNotContain("selfcontained", Body("OldLayoutFile"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selfcontained", Body("RemoveOldLayout"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if ShortSecond <> '' then Query := Query + ' OR Name = \"' + SelfContainedName + '\" OR Name = \"' + ExtractFileName(ShortSecond) + '\"';",
            Body("PoolWatchProcesses"));
        Assert.Contains("((ShortSecond <> '') and (CompareText(ShortImage, ShortSecond) = 0)) then Result := 2", Body("FromApp"));
        // CI's installer test does it on a real copy of 1.0.0-beta.1's self-contained zip (pinned by its release's SHA-256).
        string e2e = Source("ci", "installer-e2e.ps1");
        Assert.Contains("if ($zipHash -ne '0FB6DCCBF9BF7BB49EF77F3D89B6B04268F7F04F3E624CCC519E26FBD2514510') { throw", e2e);
        Assert.Contains("Expand-Archive -LiteralPath $oldZip -DestinationPath $app", e2e);
        Assert.Contains("foreach ($f in $launcher, $second) {", e2e);
        Assert.Contains("$sc = Start-Running 'tray-0b' $second\nInstall 'reinstall-0b'\nAssert-StoppedBySignal $sc", e2e.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ACleanupRunsOnceMoreWhenPartOfPoolWatchStillRunsAndAddsToTheRecord()
    {
        // A PoolWatch started after the cleanup (from the Start menu, say: its service profile can apply at its start)
        // would otherwise lose what it changed when the files go. So when part of PoolWatch still runs after the cleanup,
        // the cleanup runs once more, after which the uninstaller waits only HandWait (that cleanup has already waited for
        // PoolWatch to exit). Every run after the first that did what it was asked adds to uninstall-summary.txt
        // (--append-summary) instead of replacing it, so what the first put back stays on record; the first run, and a
        // Retry before any run was done, replace it (an earlier uninstall's record is not this one's).
        string code = Code();
        string step = code[code.IndexOf("usAppMutexCheck:", StringComparison.Ordinal)..];
        Assert.Contains("Code := RunCleanup(KeepChanges, False); Summarized := False; RanAgain := False;", step);
        Assert.Contains("AppCount := WaitForApp(App, Seconds); Again := (AppCount <> 0) and not RanAgain; " +
                        "if Again then begin RanAgain := True; Seconds := HandWait;", step);
        Assert.Contains("Code := RunCleanup(KeepChanges, True); end; until not Again;", step);
        Assert.Contains("if Append then Params := Params + ' --append-summary';", Body("RunCleanup"));
        // PoolWatch's side: --append-summary adds a section of its own, which says it is a second run.
        Assert.True(Headless.CommandLineOptions.Parse(["--uninstall-cleanup", "--append-summary"]).AppendSummary);
        string again = Program.UninstallSummary([], null, [], new DateTime(2026, 11, 1, 9, 0, 0), again: true);
        Assert.StartsWith("PoolWatch uninstall, cleanup run again, 2026-11-01 09:00", again);
        Assert.Contains("PoolWatch had made no more changes to put back.", again);
        // The two records together, read as the notices read them: what the first put back is still there.
        string dir = Path.Combine(Path.GetTempPath(), "pw-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "uninstall-summary.txt");
            var done = Row("service:a", ChangeKind.Service, "Turned off Example Device Service (ExampleSvc)");
            Program.WriteSummary(file, Program.UninstallSummary([done], null, [(done, new UndoResult(true, "It is back."))], DateTime.Now), false, p => p);
            Program.WriteSummary(file, again, true, p => p);
            string both = File.ReadAllText(file);
            Assert.Contains("Put back: " + done.What, both);
            Assert.Contains("cleanup run again", both);
            Assert.False(File.Exists(file + ".tmp"));
            // Without --append-summary a run replaces the file: the first run of an uninstall never shows an older record.
            Program.WriteSummary(file, again, false, p => p);
            Assert.DoesNotContain("Put back: ", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EachReasonTheUninstallerRemovedNothingSaysWhatToDo()
    {
        // A failure a repair cannot fix gets advice that fits it, not "repair it": the data folder (6) and the private names
        // (9) name what failed and suggest a restart; a PoolWatch that could not be started (-1) suggests the repair. A
        // PoolWatch still running (7) put nothing back. A process that still runs after a cleanup that was done is no
        // reason to remove nothing any more: what it holds goes at the next restart.
        string text = Body("NotRemovedText");
        Assert.Contains("if Code = ExitStillRunning then Result := 'PoolWatch is still running, so it put nothing back and the uninstaller has removed nothing.", text);
        Assert.Contains("else if Code = -1 then Result := 'PoolWatch could not be started for its cleanup, so it put nothing back", text);
        Assert.Contains("else if Code = ExitDataNotSafe then Result := 'PoolWatch could not make its data folder (' + ExpandConstant('{commonappdata}\\PoolWatch') + ') safe to ' + " +
                        "'use, so it put nothing back and the uninstaller has removed nothing. Close any program that may have that folder ' + " +
                        "'open, then click Retry. If this keeps happening, restart Windows, then uninstall PoolWatch again.'", text);
        Assert.Contains("else if Code = ExitNamesNotSafe then Result := 'PoolWatch could not open its private names, so it could not ask a running PoolWatch to exit: it put ' + " +
                        "'nothing back and the uninstaller has removed nothing. Click Retry to try again. If this keeps happening, restart ' + " +
                        "'Windows, then uninstall PoolWatch again.'", text);
        // The launcher's own codes (10: PoolWatch could not be started; 11: the start was refused) mean PoolWatch did not
        // run, so it put nothing back: they say so, not that it may have put back part of the changes.
        Assert.Equal((Engine.ExitCodes.NotStarted, Engine.ExitCodes.Refused), (Number("ExitNotStarted"), Number("ExitRefused")));
        Assert.Contains("else if Code = ExitNotStarted then Result := 'PoolWatch's launcher could not start PoolWatch for its cleanup (error 10), so it put " +
                        "nothing back and the uninstaller has removed nothing.", Joined(text));
        Assert.Contains("else if Code = ExitRefused then Result := 'PoolWatch refused to start its cleanup (error 11: it was started without administrator " +
                        "rights, or with .NET's start-up variables set), so it put nothing back and the uninstaller has removed nothing.", Joined(text));
        int launcherCodes = text.IndexOf("else if Code = ExitNotStarted then", StringComparison.Ordinal);
        int unknown = text.IndexOf("Result := Format('PoolWatch''s cleanup failed (error %d).", StringComparison.Ordinal);
        Assert.True(launcherCodes > 0 && unknown > launcherCodes, text);
        Assert.DoesNotContain("AppCount", text);
    }

    [Fact]
    public void WhatStopRunningAnsweredIsOnlyLogged()
    {
        // What --stop-running answers is only logged; the files decide. "Still running" (7) is not taken for
        // a reason to stop, nor any other answer, nor a PoolWatch that could not be started, for "stopped": an install
        // whose PoolWatch no longer runs can still be repaired.
        string prepare = Body("PrepareToInstall");
        // The whole statement, with the blocks around it: only when the install folder has a launcher (or its file), and
        // in the try whose finally hides the wait page; all that the try holds, so the wait follows the answer.
        string[] asking = ["if FileExists(LauncherFile(App)) or HasLauncher(App) then", "try"];
        Assert.Equal(asking, BlocksAround("PrepareToInstall", "if Launcher = '' then StopBeforeLauncher " +
            "else if Exec(Launcher, '--stop-running', '', SW_HIDE, ewWaitUntilTerminated, Code) then Log(Format('The installed PoolWatch answered --stop-running with exit code %d: " +
            "the files decide.', [Code])) else Log(Format('The installed PoolWatch could not be started to ask it to exit (error %d): the files decide.', [Code]));"));
        Assert.Equal(3, Block("PrepareToInstall", asking).Count);
        Assert.Equal(asking, BlocksAround("PrepareToInstall", "Launcher := LauncherPath(App); if Launcher = '' then StopBeforeLauncher else if Exec(Launcher, '--stop-running', " +
            "'', SW_HIDE, ewWaitUntilTerminated, Code) then Log(Format('The installed PoolWatch answered --stop-running with exit code %d: the files decide.', [Code])) " +
            "else Log(Format('The installed PoolWatch could not be started to ask it to exit (error %d): the files decide.', [Code])); WaitForApp(App, RunningWait);"));
        Assert.DoesNotContain("ExitStillRunning", prepare);
        Assert.DoesNotContain("if Code", prepare);
    }

    [Fact]
    public void AProcessOrAListThatCannotBeReadNeitherStopsNorDecides()
    {
        // A process with PoolWatch's name whose exe could not be read (it could not be opened, or Windows gave no path)
        // may run the install folder's exe, so the count is not known (-1, as for a list that cannot be read), and no
        // helper is ended while it runs. It is logged with its command line and account, and named as one Windows gave no
        // exe for. Neither stops the setup or the uninstaller any more: whether a file can be replaced or removed now is
        // asked of the file (HeldFiles), and what is in use goes at the next restart. The wait goes on only while a file is
        // still in use and something with PoolWatch's name runs (or, for a moment, BriefHold, when nothing does).
        Assert.Contains("Image := ImagePathOf(Item.ProcessId); if Image = '' then Result := -1 else", Body("FromApp"));
        string running = Body("RunningFromApp");
        Assert.Contains("else if Where < 0 then begin Unknown := Unknown + 1; if Logged then Log(Format('Process %d has PoolWatch''s name but its exe " +
                        "could not be read, so it may run from the install folder (run by %s): %s', [Id, Who, Shown])); end;", running);
        Assert.Contains("if Unknown > 0 then Result := -1 else if EndHelpers and (Result > 0) and (Others = 0) then", running);
        Assert.Contains("RunningNames := RunningNames + Format('process %d, run by %s, whose exe Windows did not give', [Id, Who]);", running);
        Assert.Contains("except Log('The processes that run from the install folder could not be listed: ' + GetExceptionMessage); Result := -1; end;", running);
        string wait = Body("WaitForApp");
        Assert.Contains("while Held and (Waited < Seconds) and ((Result <> 0) or (Waited < BriefHold)) do", wait);
        Assert.Contains("RunningNames := '';", wait);
        Assert.Contains("Listed := True; try Service := ConnectWmi; except", wait);
        Assert.DoesNotContain("CannotCheckReason", Script());
        Assert.DoesNotContain("could not check whether PoolWatch still runs", Script());
    }

    [Fact]
    public void AnySpellingOfTheInstallFolderCounts()
    {
        // The exe's path comes from the kernel (QueryFullProcessImageNameW through ImagePathOf). It gives the real folder
        // for a process started through a junction or a subst drive that points into the install folder, but keeps a short
        // (8.3) or mixed spelling as the process was started; comparing the two paths in their short form covers those.
        // WMI's own ExecutablePath, which it reads from the process's memory and which would show the junction's or the
        // subst drive's spelling, is never asked for or compared.
        Assert.Contains("ShortExe := GetShortName(Exe);", Body("RunningFromApp"));
        Assert.Contains("Image := ImagePathOf(Item.ProcessId); if Image = '' then Result := -1 else begin ShortImage := GetShortName(Image); " +
                        "if CompareText(ShortImage, ShortExe) = 0 then Result := 1 else if ((ShortLauncher <> '') and (CompareText(ShortImage, ShortLauncher) = 0)) or " +
                        "((ShortSecond <> '') and (CompareText(ShortImage, ShortSecond) = 0)) then Result := 2 else Result := 0; end;", Body("FromApp"));
        Assert.Contains("ShortLauncher := ''; if Launcher <> '' then ShortLauncher := GetShortName(Launcher); ShortSecond := ''; if Second <> '' then " +
                        "ShortSecond := GetShortName(Second);", Body("RunningFromApp"));
        Assert.Contains("if QueryFullProcessImageNameW(Handle, 0, Buffer, Size) then Result := Copy(Buffer, 1, Size);", Body("ImagePathOf"));
        Assert.DoesNotContain("ExecutablePath", Script()); // the path WMI reads from the process's memory is never used
        // Each function takes the install folder or the exes it looks for, so the layout of the install folder is decided in
        // one place each (LauncherFile, ProgramPath, HasLauncher), and the list of processes goes by the files' names.
        Assert.Contains("Second := ''; if HasLauncher(App) then begin Exe := ProgramPath(App); Launcher := LauncherFile(App); " +
                        "if FileExists(SecondLauncherPath(App)) and (CompareText(Launcher, SecondLauncherPath(App)) <> 0) then Second := SecondLauncherPath(App); " +
                        "end else begin Exe := LauncherFile(App); Launcher := ''; end;", Body("WaitForApp"));
        Assert.Contains("AppCount := WaitForApp(App, Seconds);", Code());
        foreach (var name in new[] { "RunningFromApp", "FromApp", "EndHelper", "WaitForApp", "HeldFiles", "FindHeld", "WhenToReplace" })
            Assert.DoesNotContain("{app}", Body(name));
    }

    [Fact]
    public void AHelperThatOutlivesPoolWatchIsEndedOnlyWhenNothingElseOfPoolWatchRuns()
    {
        // A token-leak test helper holds no mutex and can run on after its PoolWatch is gone, waiting for a click on its
        // notice on any desktop (another session's included). The setup and the uninstaller end it themselves, by process,
        // so a silent run never waits on a click, also when the installed PoolWatch's own --stop-running ends no helper (an
        // earlier one's did not). Only when every process that runs the exe is a helper: while anything else of PoolWatch
        // runs, or might (a path Windows did not give), its helpers are its own.
        string running = Body("RunningFromApp");
        // The launcher counts as something else of PoolWatch: it runs for as long as the PoolWatch it started.
        Assert.Contains("Result := Result + 1; if (Where = 2) or not IsHelperLine(Line) then Others := Others + 1;", running);
        Assert.Contains("if Unknown > 0 then Result := -1 else if EndHelpers and (Result > 0) and (Others = 0) then for I := 0 to Total - 1 do " +
                        "begin Item := Found.ItemIndex(I); if FromApp(Item, ShortExe, ShortLauncher, ShortSecond) = 1 then begin Id := Item.ProcessId; " +
                        "EndHelper(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id); end; end;", running);
        Assert.Contains("Result := RunningFromApp(Service, Exe, Launcher, Second, True, True)", Body("WaitForApp"));
        Assert.Contains("Result := RunningFromApp(Service, Exe, Launcher, Second, False, True);", Body("WaitForApp"));
        Assert.Equal(Engine.LeftoverHelpers.HelperOption, Constant("HelperOption"));
        // Opened first, which keeps its process ID from going to another process; read again by that ID; ended through that
        // handle only if it is still a helper running the install folder's exe; the handle is always closed.
        string end = Body("EndHelper");
        int open = end.IndexOf("Opened := OpenProcess(EndRights, False, Id);", StringComparison.Ordinal);
        int read = end.IndexOf("Found := PoolWatchProcesses(Service, Exe, ShortExe, ShortLauncher, ShortSecond, Id);", StringComparison.Ordinal);
        int check = end.IndexOf("if (FromApp(Item, ShortExe, ShortLauncher, ShortSecond) <> 1) or not IsHelperLine(CommandLineOf(Item)) then", StringComparison.Ordinal);
        int kill = end.IndexOf("else if TerminateProcess(Opened, 1) then", StringComparison.Ordinal);
        Assert.True(open >= 0 && open < read && read < check && check < kill, end);
        Assert.Single(Regex.Matches(Code(), @"\bTerminateProcess\(Opened"));
        Assert.EndsWith("CloseHandle(Opened); end;", Whole("EndHelper"));
        Assert.Equal(0x0001 | 0x1000, Number16("EndRights")); // PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION
        // Nothing is closed by its window any more: a window on another desktop could not be reached.
        Assert.DoesNotContain("FindWindow", Script());
        Assert.DoesNotContain("PostMessage", Script());
    }

    /// <summary>A hexadecimal constant of the script's [Code] ($1001).</summary>
    private static int Number16(string name) => Convert.ToInt32(Regex.Match(Script(), $@"(?m)^\s*{name} = \$([0-9A-Fa-f]+);").Groups[1].Value, 16);

    /// <summary>
    /// The script's IsHelperLine, line for line (its CharAt gives #0 outside the text, and Copy is Pascal's, from 1); the
    /// first test holds the script's text, so the two change together.
    /// </summary>
    private static bool ScriptIsHelperLine(string line)
    {
        string option = Constant("HelperOption");
        char CharAt(int i) => i >= 1 && i <= line.Length ? line[i - 1] : '\0';
        string Copy(int index, int count) => index > line.Length ? "" : line.Substring(index - 1, Math.Min(count, line.Length - index + 1));
        int i = 1;
        while (CharAt(i) == ' ' || CharAt(i) == '\t') i++;
        if (CharAt(i) == '"')
        {
            i++;
            while (CharAt(i) != '"' && CharAt(i) != '\0') i++;
            if (CharAt(i) == '\0') return false;
            i++;
        }
        else
            while (CharAt(i) != ' ' && CharAt(i) != '\t' && CharAt(i) != '\0') i++;
        if (CharAt(i) != ' ' && CharAt(i) != '\t') return false;
        while (CharAt(i) == ' ' || CharAt(i) == '\t') i++;
        return Copy(i, option.Length) == option &&
               (CharAt(i + option.Length) == '\0' || CharAt(i + option.Length) == ' ' || CharAt(i + option.Length) == '\t');
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test 64 \"C:\\x.json\"", true)]
    [InlineData("C:\\PROGRA~1\\POOLWA~1\\POOLWA~1.EXE --token-leak-test 64 C:\\x.json", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"\t--token-leak-test", true)]
    [InlineData("  \"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"   --token-leak-test 64", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-testing 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --tray --token-leak-test 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"--token-leak-test 64", false)]
    [InlineData("\"C:\\a --token-leak-test b\\PoolWatch.exe\" --tray", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --TOKEN-LEAK-TEST 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" ", false)]
    [InlineData("PoolWatch.exe", false)]
    [InlineData("--token-leak-test", false)]
    [InlineData("", false)]
    // The two readers' rules, on the lines where a reader that split on any white space or control character, or read past
    // a NUL, would answer otherwise. Only a space and a tab separate: an ideographic space before an unquoted name leaves
    // the option its first argument (true); a no-break or em space, a CR or an LF where a separator would be wanted
    // leaves the option inside a word (false). An unquoted program name runs on through a CR, an LF or another control
    // character up to the first space or tab (true when the option follows that space; false when it does not). A NUL
    // ends the line. These are the readers' rules, not Windows' own split of a command line, which differs on some of
    // these lines (it ends an unquoted program name at a control character); both readers end only a process that runs
    // this very exe.
    [InlineData("\u3000\"C:\\x --token-leak-test y\"", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"\u00A0--token-leak-test 64", false)]
    [InlineData("\n\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test\r", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" \u2003--token-leak-test 64", false)]
    [InlineData("C:\\x\n --token-leak-test", true)]
    [InlineData("C:\\x\r --token-leak-test 64", true)]
    [InlineData("C:\\x\u0001 --token-leak-test", true)]
    [InlineData("C:\\x\n--token-leak-test", false)]
    [InlineData("\"C:\\x\" --token-leak-test\0tail", true)]
    [InlineData("\"C:\\x\0\" --token-leak-test", false)]
    [InlineData("C:\\x\0 --token-leak-test", false)]
    [InlineData("\"C:\\x\" --tok\0en-leak-test", false)]
    public void TheScriptKnowsAHelperByItsCommandLineAsPoolWatchDoes(string commandLine, bool helper)
    {
        // The setup and the uninstaller end only what PoolWatch's own step would (LeftoverHelpers.IsHelper): the same
        // answer for every line listed, a line Windows did not give ('') included.
        Assert.Equal(helper, ScriptIsHelperLine(commandLine));
        Assert.Equal(helper, Engine.LeftoverHelpers.IsHelper(commandLine));
    }

    [Fact]
    public void TheBoxesNameEachFileInUseAndWhatHasItOpen()
    {
        // When another account's program has a file in use, closing PoolWatch from a tray icon does not help: the Finished
        // page and the setup's log name each file in use with what has it open (Restart Manager: the program, its process
        // ID and its account), as it is after the setup ended what it may end, and so does the uninstaller's log when it
        // writes one; the uninstaller's notice, which has to fit on the screen, names the first MaxShown, says how many
        // more, and, when there is a log, that the log names each. The log also names each process with PoolWatch's name
        // the wait saw, with its account and command line, saying which ones Windows gave no exe for.
        string running = Body("RunningFromApp");
        Assert.Contains("if Where = 2 then Which := 'its launcher, ' else Which := '';", running);
        Assert.Contains("if Where <> 0 then begin Who := OwnerOf(Item); if Named < MaxProcessesShown then begin if RunningNames <> '' then " +
                        "RunningNames := RunningNames + '; '; if Where > 0 then RunningNames := RunningNames + Format('%sprocess %d, run by %s', [Which, Id, Who]) " +
                        "else RunningNames := RunningNames + Format('process %d, run by %s, whose exe Windows did not give', [Id, Who]); Named := Named + 1; end; end;", running);
        Assert.Contains("if Logged then Log(Format('PoolWatch still runs from the install folder: %sprocess %d, run by %s: %s', [Which, Id, Who, Shown]));", running);
        Assert.Contains("Owner := Item.ExecMethod_('GetOwner');", Body("OwnerOf"));
        Assert.Contains("SELECT Handle, ProcessId,", Body("PoolWatchProcesses")); // the key, which a method call needs
        // What has a file open is named where the file is: in the setup's list of files in use (the Finished page and its
        // log, from the one look that decides), and in what the uninstaller gives Windows to delete at the next restart.
        Assert.Contains("Holders := HoldersOf(Path); AddShown(HeldList, HeldCount, Rel + Name + Holders);", Whole("FindHeld"));
        Assert.Contains("if Held then begin FinishFiles := HeldAll; FinishAtNextRestart(", Whole("WhenToReplace"));
        Assert.Contains("DeleteOneAtRestart(Dir + '\\' + Name, Rel + Name, HoldersOf(Dir + '\\' + Name));", Whole("DeleteAtRestart"));
        Assert.Contains("AddShown(RestartList, RestartCount, Shown + Holders);", Whole("DeleteOneAtRestart"));
        // The uninstaller writes a log only when it was started with /LOG (as Apps and Features starts it: the {log}
        // constant is '' otherwise), so the notice points to its log, by its name, only when there is one.
        Assert.Contains("', with the folders left in it, when it next restarts.'; if (RestartCount > MaxShown) and (ExpandConstant('{log}') <> '') then " +
                        "Result := Result + ' The uninstaller''s log, ' + ExpandConstant('{log}') + ', names each of them.';", Whole("RestartText"));
        Assert.DoesNotContain("TEMP folder", Whole("RestartText"));
        Assert.Matches(@"(?m)^UninstallLogging=yes\r?$", Section("Setup"));
        Assert.Contains("Log('Windows deletes it at its next restart: ' + Path + Holders);", Whole("DeleteOneAtRestart"));
        // The processes with PoolWatch's name are only logged now: the last look at them can name a helper the wait ended.
        Assert.DoesNotContain("RunningNames", Whole("WhenToReplace"));
        Assert.DoesNotContain("RunningNames", Whole("RestartText"));
        Assert.DoesNotContain("RunningNames", Whole("FinishText"));
        // The account: from the process's owner, through Windows' list of processes, over one WMI connection kept for the
        // run, and read once per process for each logged look (a process that has many files open is asked about once).
        string account = Whole("AccountOf");
        Assert.Contains("Key := '|' + IntToStr(Id) + '='; P := Pos(Key, Accounts); if P > 0 then begin", account);
        Assert.Contains("if AccountWmi = 0 then begin AccountWmi := -1; AccountService := ConnectWmi; AccountWmi := 1; end; if AccountWmi = 1 then begin " +
                        "Found := AccountService.ExecQuery('SELECT Handle, ProcessId FROM Win32_Process WHERE ProcessId = ' + IntToStr(Id)); if Found.Count = 1 then " +
                        "Result := OwnerOf(Found.ItemIndex(0)); end;", account);
        Assert.EndsWith("Accounts := Accounts + Key + Result + '|'; end;", account);
        Assert.DoesNotContain("ConnectWmi", Whole("HoldersOf"));
        Assert.Contains("if Logged then Accounts := '';", Whole("HeldFiles"));
        Assert.Contains("RestartFailed := 0; Accounts := '';", Whole("RemoveRest"));
    }

    [Fact]
    public void TheSetupsPageKeepsDrawingWhileItWaits()
    {
        // Run by hand, the wait is on a page of its own whose bar moves a step four times a second, and each step lets the
        // wizard draw and answer Windows; a page that cannot be shown leaves the wait on the Preparing page. Never a Sleep
        // of a whole second, which kept the page from drawing until the wait was over.
        Assert.Contains("WaitPage := CreateOutputProgressPage('Closing PoolWatch',", Body("InitializeWizard"));
        Assert.Contains("for Quarter := 1 to 4 do begin KeepDrawn(Waited * 4 + Quarter, Seconds * 4); Sleep(250); if StoppingOld then SetOldExit(False); end;", Body("WaitForApp"));
        Assert.Contains("if WaitPage <> nil then WaitPage.SetProgress(Step, Steps);", Body("KeepDrawn"));
        Assert.Contains("ShowWaitPage; try Launcher := LauncherPath(App); if Launcher = '' then StopBeforeLauncher else if Exec(Launcher, '--stop-running',", Body("PrepareToInstall"));
        Assert.Contains("finally HideWaitPage; end;", Body("PrepareToInstall"));
        Assert.Contains("if (WaitPage = nil) or WizardSilent then Exit; try WaitPage.SetText(", Body("ShowWaitPage"));
        Assert.DoesNotMatch(@"Sleep\((?!250\))", Code());
    }

    /// <summary>
    /// The setup's start and its pages, whole: the note a PC with an Arm processor gets when the setup is not silent
    /// (InitializeSetup), what lays out the Welcome, Ready and Finished pages (NewLabel, StoredFontHeight, StoredIconSize,
    /// NewIcon, LayOutWelcomePage, NewStep, CreateReadyPage, LayOutReadyPage, LayOutFinishedPage), and when each is laid
    /// out, with the wait page (InitializeWizard, CurPageChanged). Each routine is held from its header to its last end;,
    /// its comments included, with every run of white space made one space, so any edit to one fails here unless it only
    /// changes how long a run of white space is, or which white space it is. InstallerPagesTests says what the pages show
    /// and how they fit; the note at the start is held here only.
    /// </summary>
    [Fact]
    public void TheSetupsStartAndPagesAreHeldWhole()
    {
        Assert.Equal(OneLine("""
            function InitializeSetup: Boolean;
            begin
              { Shown only to someone at the setup: a silent install must never wait for a click. }
              if IsArm64 and not WizardSilent then
                MsgBox('PoolWatch is built for x64 PCs. Windows on Arm runs it through its x64 emulation, where PoolWatch has not been tested yet.',
                  mbInformation, MB_OK);
              Result := True;
            end;
            """), Whole("InitializeSetup"));
        Assert.Equal(OneLine("""
            function NewLabel(Parent: TWinControl; Left, Top, Width: Integer; const Caption: String; Bold: Boolean;
              TextColor: TColor): TNewStaticText;
            begin
              Result := TNewStaticText.Create(WizardForm);
              Result.Parent := Parent;
              Result.AutoSize := False;
              Result.WordWrap := True;
              Result.Left := Left;
              Result.Top := Top;
              Result.Width := Width;
              if Bold then
                Result.Font.Style := [fsBold];
              if TextColor <> clNone then
                Result.Font.Color := TextColor;
              Result.Caption := Caption;
              Result.AdjustHeight;
            end;
            """), Whole("NewLabel"));
        Assert.Equal(OneLine("""
            function StoredFontHeight(Level: Integer): Integer;
            begin
              case Level of
                0: Result := FontHeightAt100;
                1: Result := 20;
                2: Result := 25;
                3: Result := 30;
              else
                Result := 32;
              end;
            end;
            """), Whole("StoredFontHeight"));
        Assert.Equal(OneLine("""
            function StoredIconSize(Base, Size: Integer): Integer;
            var
              Level: Integer;
            begin
              Level := 0;
              Result := Base;
              while (Result < Size) and (Level < 4) do
              begin
                Level := Level + 1;
                Result := (2 * Base * StoredFontHeight(Level) + FontHeightAt100) div (2 * FontHeightAt100);
              end;
            end;
            """), Whole("StoredIconSize"));
        Assert.Equal(OneLine("""
            function NewIcon(Parent: TWinControl; const Name: String; Base, Left, Top: Integer): TBitmapImage;
            var
              Size, Stored: Integer;
              FileName, Path: String;
            begin
              Size := ScaleX(Base);
              Stored := StoredIconSize(Base, Size);
              FileName := Format('ready-%s-%d.png', [Name, Stored]);
              Path := ExpandConstant('{tmp}\') + FileName;
              Result := TBitmapImage.Create(WizardForm);
              Result.Parent := Parent;
              Result.BackColor := clNone;
              Result.Stretch := Stored <> Size;
              Result.Left := Left;
              Result.Top := Top;
              Result.Width := Size;
              Result.Height := Size;
              try
                if not FileExists(Path) then
                  ExtractTemporaryFile(FileName);
                Result.PngImage.LoadFromFile(Path);
              except
                Log('The Ready page''s icon ' + FileName + ' could not be shown: ' + GetExceptionMessage);
              end;
            end;
            """), Whole("NewIcon"));
        Assert.Equal(OneLine("""
            procedure LayOutWelcomePage;
            var
              Mission, SetupText, ClickNextText: TNewStaticText;
            begin
              Mission := WizardForm.WelcomeLabel2;
              Mission.Font.Size := Mission.Font.Size + 1;
              Mission.Caption := SetupMessage(msgWelcomeLabel2);
              Mission.AdjustHeight;
              SetupText := NewLabel(Mission.Parent, Mission.Left, Mission.Top + Mission.Height + ScaleY(12), Mission.Width,
                CustomMessage('WelcomeSetup'), False, clNone);
              ClickNextText := NewLabel(Mission.Parent, Mission.Left, SetupText.Top + SetupText.Height + ScaleY(12), Mission.Width,
                SetupMessage(msgClickNext), False, MutedColor);
              Log(Format('Welcome page, %d x %d: the text ends at %d.', [
                Mission.Parent.Width, Mission.Parent.Height, ClickNextText.Top + ClickNextText.Height]));
            end;
            """), Whole("LayOutWelcomePage"));
        Assert.Equal(OneLine("""
            procedure NewStep(I, StepWidth: Integer; const IconName, Title, Line: String);
            var
              X: Integer;
            begin
              X := I * (StepWidth + ScaleX(28));
              StepIcons[I] := NewIcon(WizardForm.ReadyPage, IconName, StepIconSize, X, ScaleY(4));
              StepTitles[I] := NewLabel(WizardForm.ReadyPage, X, 0, StepWidth, Title, True, clNone);
              StepTexts[I] := NewLabel(WizardForm.ReadyPage, X, 0, StepWidth, Line, False, MutedColor);
              if I < 2 then
                NextIcons[I] := NewIcon(WizardForm.ReadyPage, 'next', NextIconSize, X + StepWidth + ScaleX(6),
                  ScaleY(4) + (ScaleX(StepIconSize) - ScaleX(NextIconSize)) div 2);
            end;
            """), Whole("NewStep"));
        Assert.Equal(OneLine("""
            procedure CreateReadyPage;
            var
              PageWidth, StepWidth, TextLeft: Integer;
            begin
              PageWidth := WizardForm.InnerNotebook.Width;
              StepWidth := (PageWidth - 2 * ScaleX(28)) div 3;
              NewStep(0, StepWidth, 'install', CustomMessage('ReadyInstallTitle'), '');
              NewStep(1, StepWidth, 'tray', CustomMessage('ReadyTrayTitle'), CustomMessage('ReadyTrayText'));
              NewStep(2, StepWidth, 'setup', CustomMessage('ReadySetupTitle'), CustomMessage('ReadySetupText'));

              SafePanel := TPanel.Create(WizardForm);
              SafePanel.Parent := WizardForm.ReadyPage;
              SafePanel.BevelOuter := bvNone;
              SafePanel.ParentBackground := False;
              SafePanel.Color := SafeBackColor;
              SafePanel.Caption := '';
              SafePanel.Left := 0;
              SafePanel.Width := PageWidth;
              SafeBar := TPanel.Create(WizardForm);
              SafeBar.Parent := SafePanel;
              SafeBar.BevelOuter := bvNone;
              SafeBar.ParentBackground := False;
              SafeBar.Color := SafeColor;
              SafeBar.Caption := '';
              SafeBar.Left := 0;
              SafeBar.Top := 0;
              SafeBar.Width := ScaleX(3);
              SafeIcon := NewIcon(SafePanel, 'safe', SafeIconSize, ScaleX(3 + 14), ScaleY(12));
              TextLeft := SafeIcon.Left + SafeIcon.Width + ScaleX(12);
              SafeTitle := NewLabel(SafePanel, TextLeft, ScaleY(12), PageWidth - TextLeft - ScaleX(14),
                CustomMessage('ReadySafeTitle'), True, SafeColor);
              SafeText := NewLabel(SafePanel, TextLeft, 0, SafeTitle.Width, CustomMessage('ReadySafeText'), False, InkColor);
              SafeTitle.Color := SafeBackColor;
              SafeText.Color := SafeBackColor;
              WizardForm.ReadyLabel.Font.Color := MutedColor;
            end;
            """), Whole("CreateReadyPage"));
        Assert.Equal(OneLine("""
            procedure LayOutReadyPage;
            var
              I, Bottom: Integer;
            begin
              WizardForm.ReadyMemo.Visible := False;
              StepTexts[0].Caption := FmtMessage(CustomMessage('ReadyInstallText'), [WizardDirValue]);
              Bottom := 0;
              for I := 0 to 2 do
              begin
                StepTitles[I].Top := StepIcons[I].Top + StepIcons[I].Height + ScaleY(9);
                StepTitles[I].AdjustHeight;
                StepTexts[I].Top := StepTitles[I].Top + StepTitles[I].Height + ScaleY(4);
                StepTexts[I].AdjustHeight;
                if StepTexts[I].Top + StepTexts[I].Height > Bottom then
                  Bottom := StepTexts[I].Top + StepTexts[I].Height;
              end;
              SafePanel.Top := Bottom + ScaleY(26);
              SafeText.Top := SafeTitle.Top + SafeTitle.Height + ScaleY(3);
              SafeText.AdjustHeight;
              Bottom := SafeText.Top + SafeText.Height;
              if SafeIcon.Top + SafeIcon.Height > Bottom then
                Bottom := SafeIcon.Top + SafeIcon.Height;
              SafePanel.Height := Bottom + ScaleY(12);
              SafeBar.Height := SafePanel.Height;
              WizardForm.ReadyLabel.Top := WizardForm.InnerNotebook.Height - WizardForm.ReadyLabel.Height - ScaleY(10);
              if WizardForm.ReadyLabel.Top < SafePanel.Top + SafePanel.Height + ScaleY(8) then
                WizardForm.ReadyLabel.Top := SafePanel.Top + SafePanel.Height + ScaleY(8);
              Log(Format('Ready page, %d x %d: steps end at %d, the panel runs from %d to %d, the last line runs from %d to %d.', [
                WizardForm.InnerNotebook.Width, WizardForm.InnerNotebook.Height, SafePanel.Top - ScaleY(26), SafePanel.Top,
                SafePanel.Top + SafePanel.Height, WizardForm.ReadyLabel.Top,
                WizardForm.ReadyLabel.Top + WizardForm.ReadyLabel.Height]));
            end;
            """), Whole("LayOutReadyPage"));
        Assert.Equal(OneLine("""
            procedure LayOutFinishedPage;
            var
              Delta: Integer;
              Files: TNewMemo;
            begin
              if WizardForm.YesRadio.Visible then
                Exit;
              Delta := WizardForm.FinishedLabel.Height;
              if FinishAtRestart then
              begin
                WizardForm.FinishedHeadingLabel.Caption := FinishHeading;
                WizardForm.FinishedLabel.Caption := FinishText + #13#10;
              end
              else
                WizardForm.FinishedLabel.Caption := SetupMessage(msgFinishedLabel) + #13#10;
              WizardForm.FinishedLabel.AdjustHeight;
              Delta := WizardForm.FinishedLabel.Height - Delta;
              WizardForm.RunList.Top := WizardForm.RunList.Top + Delta;
              WizardForm.RunList.Height := WizardForm.RunList.Height - Delta;
              if FinishAtRestart and (FinishFiles <> '') then
              begin
                Files := TNewMemo.Create(WizardForm);
                Files.Parent := WizardForm.FinishedPage;
                Files.Left := WizardForm.RunList.Left;
                Files.Top := WizardForm.RunList.Top;
                Files.Width := WizardForm.RunList.Width;
                Files.Height := WizardForm.RunList.Height;
                if Files.Height < ScaleY(48) then
                  Files.Height := ScaleY(48);
                Files.ReadOnly := True;
                Files.ScrollBars := ssVertical;
                Files.Text := Copy(FinishFiles, 3, Length(FinishFiles));
                Log(Format('Finished page, %d high: the files in use are listed from %d to %d.', [WizardForm.FinishedPage.Height,
                  Files.Top, Files.Top + Files.Height]));
              end;
            end;
            """), Whole("LayOutFinishedPage"));
        Assert.Equal(OneLine("""
            procedure InitializeWizard;
            begin
              LayOutWelcomePage;
              CreateReadyPage;
              { Shown only while the setup waits for PoolWatch to close (ShowWaitPage, in PrepareToInstall). }
              WaitPage := CreateOutputProgressPage('Closing PoolWatch', 'Setup waits until no part of PoolWatch runs, so that its files can be replaced.');
            end;
            """), Whole("InitializeWizard"));
        Assert.Equal(OneLine("""
            procedure CurPageChanged(CurPageID: Integer);
            begin
              case CurPageID of
                wpReady: LayOutReadyPage;
                wpFinished: LayOutFinishedPage;
              end;
            end;
            """), Whole("CurPageChanged"));
    }

    [Fact]
    public void AnUninstallStartedByHandWaitsOnlyAFewSeconds()
    {
        // The uninstaller has no page that keeps drawing during a wait, so started by hand it waits HandWait seconds (less
        // than the 5 s after which Windows calls a window that does not answer "not responding"). A silent uninstall shows
        // nothing, and waits the whole RunningWait. After a second cleanup, which has already waited for PoolWatch to
        // exit, any uninstall waits HandWait.
        Assert.Contains("if UninstallSilent then Seconds := RunningWait else Seconds := HandWait;", Code());
        Assert.True(Number("HandWait") < 5);
        Assert.Equal(3, Regex.Matches(Code(), @"\bWaitForApp\(").Count); // its own header, the setup's call and the uninstaller's
        Assert.Contains("WaitForApp(App, RunningWait);", Body("PrepareToInstall"));
    }

    [Fact]
    public void ADeleteDataUninstallStartedByHandSaysWhatItPutBack()
    {
        // Started by hand with /DELETEDATA, it asks nothing about the changes, so once every change is back it says what it
        // put back, and that the data folder is to be deleted too (the box comes before the delete, which may leave what
        // another program has in use: SayDataLeft then says what); then it deletes the folder, as asked. The folder takes
        // uninstall-summary.txt with it, so the changes put back only in part that the notice counts are named in the boxes
        // after it (TheDeleteDataNoticesNameEveryChangePutBackOnlyInPart).
        // The notice's statements, whole, with the blocks around them: all that the block for a /DELETEDATA uninstall
        // started by hand holds.
        Assert.Equal(["if HasSwitch('DELETEDATA') then", "if not UninstallSilent then"], BlocksAround("DeleteDataWanted", "Text := PutBackDone(DataDir); " +
            "if SummaryNamesMore(DataDir, PartCount) then if PartCount > MaxShown + 1 then Text := Text + #13#10#13#10 + " +
            "Format('The other %d changes put back only in part are named next, ' + 'with what was not done.', [PartCount - MaxShown]) " +
            "else if PartCount = MaxShown + 1 then Text := Text + #13#10#13#10 + 'The other change put back only in part is named next, with what was not done.'; " +
            "MsgBox(Text + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are to be deleted too.', mbInformation, MB_OK); ShowRestPutBackInPart(DataDir);"));
        Assert.Equal(4, Block("DeleteDataWanted", "if HasSwitch('DELETEDATA') then", "if not UninstallSilent then").Count);
        Assert.Equal(["if HasSwitch('DELETEDATA') then"], BlocksAround("DeleteDataWanted", "if not UninstallSilent then begin ... end; Result := True;"));
        // Only reached once every change was put back; the delete, then what it left.
        Assert.Equal(["case CurUninstallStep of usAppMutexCheck: begin ... end; usPostUninstall:"],
            BlocksAround("CurUninstallStepChanged", "if ChangesPutBack then if DeleteDataWanted(DataDir) then begin ... end;"));
        Assert.Equal(["DelTree(DataDir, True, True, True)", "SayDataLeft(DataDir)"], Block("CurUninstallStepChanged", DeletingTheData));
        // The box says the folder is to be deleted, never that it is: the words the box said before are not in it.
        Assert.DoesNotContain(") are deleted too.", Body("DeleteDataWanted"));
    }

    /// <summary>
    /// After the data folder's delete (DelTree, whose result Inno Setup gives no caller), the uninstaller looks whether
    /// anything of the folder is left (SayDataLeft: the folder there at all, DirExists) and, if so, logs it and says it in
    /// a box when someone is there: each file left with what has it open (FindDataLeft, HoldersOf), the first MaxShown in
    /// the box and how many more, and what to do. A link it finds is not listed through, the folder itself included
    /// (IsLink), and no holder is asked about a link. What is left is not given to Windows to delete at the next
    /// restart, as the install folder's rest is (RemoveRest): a PoolWatch installed again before then keeps its data in
    /// the same folder, under the same names. Both routines are held whole, and the box's words are held through
    /// <see cref="DataLeftTextAsTheScript"/>.
    /// </summary>
    [Fact]
    public void WhatIsLeftOfTheDataFolderIsSaidAfterItsDeleteNeverReadThroughALinkAndNeverLeftToTheRestart()
    {
        Assert.Equal(OneLine("""
            procedure FindDataLeft(const Dir, Rel: String; var List: String; var Count: Integer);
            var
              Find: TFindRec;
              Name, Holders: String;
            begin
              if FindFirst(Dir + '\*', Find) then
              try
                repeat
                  Name := Find.Name;
                  if (Name <> '.') and (Name <> '..') then
                  begin
                    if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then
                      FindDataLeft(Dir + '\' + Name, Rel + Name + '\', List, Count)
                    else
                    begin
                      Holders := '';
                      if (Find.Attributes and AttrLink) = 0 then
                        Holders := HoldersOf(Dir + '\' + Name);
                      AddShown(List, Count, Rel + Name + Holders);
                      Log('Left in the data folder: ' + Dir + '\' + Name + Holders);
                    end;
                  end;
                until not FindNext(Find);
              finally
                FindClose(Find);
              end;
            end;
            """), Whole("FindDataLeft"));
        Assert.Equal(OneLine("""
            procedure SayDataLeft(const Dir: String);
            var
              List, Text: String;
              Count: Integer;
            begin
              if not DirExists(Dir) then
                Exit;
              List := '';
              Count := 0;
              Accounts := '';
              if not IsLink(Dir) then
                FindDataLeft(Dir, '', List, Count);
              Text := 'PoolWatch''s reports, logs and settings could not all be deleted: what is left of ' + Dir + ' is still in ' +
                'use, or could not be deleted for another reason.';
              if Count > 0 then
                Text := Text + ' These files are left:' + List + MoreLine(Count);
              Text := Text + #13#10#13#10 + 'Delete what is left of ' + Dir + ' yourself once no program has it in use.';
              Log(Text);
              if not UninstallSilent then
                MsgBox(Text, mbInformation, MB_OK);
            end;
            """), Whole("SayDataLeft"));
        // Called once, right after the delete, in the block that deletes the folder.
        Assert.Equal(["DelTree(DataDir, True, True, True)", "SayDataLeft(DataDir)"], Block("CurUninstallStepChanged", DeletingTheData));
        Assert.Equal(["procedure SayDataLeft(", "SayDataLeft("], Regex.Matches(Code(), @"(procedure )?SayDataLeft\(").Select(m => m.Value));
        // Its one box is shown only when someone is there; a silent uninstall logs the same text.
        Assert.Empty(BlocksAround("SayDataLeft", "Log(Text); if not UninstallSilent then MsgBox(Text, mbInformation, MB_OK);"));
        Assert.Single(Regex.Matches(Body("SayDataLeft"), @"MsgBox\("));
        // Nothing of the data folder is given to Windows for the restart, and nothing of it is listed through a link it finds.
        Assert.DoesNotContain("RestartReplace", Body("SayDataLeft") + Body("FindDataLeft"));
        Assert.DoesNotContain("DeleteOneAtRestart", Body("SayDataLeft") + Body("FindDataLeft"));
        Assert.Equal(["if FindFirst(Dir + '\\*', Find) then try", "repeat ... until not FindNext(Find)", "if (Name <> '.') and (Name <> '..') then"],
            BlocksAround("FindDataLeft", "if ((Find.Attributes and AttrFolder) <> 0) and ((Find.Attributes and AttrLink) = 0) then " +
                "FindDataLeft(Dir + '\\' + Name, Rel + Name + '\\', List, Count) else begin ... end;"));
        Assert.Empty(BlocksAround("SayDataLeft", "if not IsLink(Dir) then FindDataLeft(Dir, '', List, Count);"));

        // The words, as the script puts them together.
        const string Dir = @"C:\ProgramData\PoolWatch";
        Assert.Equal("PoolWatch's reports, logs and settings could not all be deleted: what is left of C:\\ProgramData\\PoolWatch is still in use, " +
                     "or could not be deleted for another reason.\r\n\r\nDelete what is left of C:\\ProgramData\\PoolWatch yourself once no program has it in use.",
            DataLeftTextAsTheScript(Dir, []));
        var twelve = Enumerable.Range(1, 12).Select(i => $@"data\2026110{i % 10}\samples.csv (in use by x.exe, process {i}, run by PC\someone)").ToList();
        string text = DataLeftTextAsTheScript(Dir, twelve);
        Assert.Contains(" These files are left:\r\n- " + twelve[0] + "\r\n- " + twelve[1], text);
        Assert.Contains("\r\n- " + twelve[9] + "\r\nAnd 2 more.\r\n\r\nDelete what is left", text);
        Assert.DoesNotContain(twelve[10], text);
    }

    /// <summary>
    /// The text SayDataLeft logs and shows, as the script puts it together (AddShown, MoreLine): what is left of
    /// <paramref name="dir"/>, the files left (<paramref name="left"/>, each with what has it open, as FindDataLeft adds
    /// them), the first MaxShown named and the rest counted, then what to do.
    /// </summary>
    private static string DataLeftTextAsTheScript(string dir, IReadOnlyList<string> left)
    {
        int maxShown = Number("MaxShown"), maxLine = Number("MaxLineShown");
        string text = $"PoolWatch's reports, logs and settings could not all be deleted: what is left of {dir} is still in use, or could not be deleted for another reason.";
        if (left.Count > 0)
            text += " These files are left:" + string.Concat(left.Take(maxShown).Select(l => "\r\n- " + Shorten(l, maxLine))) +
                    (left.Count > maxShown ? $"\r\nAnd {left.Count - maxShown} more." : "");
        return text + $"\r\n\r\nDelete what is left of {dir} yourself once no program has it in use.";
    }

    /// <summary>
    /// SECURITY.md says what of the data folder the uninstaller names as left as SayDataLeft's box does: what is still in
    /// use, or was not deleted for another reason (the box: "is still in use, or could not be deleted for another
    /// reason"), not only what another program has in use.
    /// </summary>
    [Fact]
    public void SecurityMdSaysWhatIsLeftOfTheDataFolderAsTheBoxDoes()
    {
        Assert.Contains("is still in use, or could not be deleted for another reason.", DataLeftTextAsTheScript(@"C:\x", []));
        Assert.Contains("after deleting it, says what of it is left, still in use by another program (a PoolWatch still running keeps the " +
                        "folder itself open) or not deleted for another reason, in its log", OneLine(Source("SECURITY.md")));
    }

    [Fact]
    public void TheInstallerTestLooksForWhatTheScriptWritesAndRunsWithoutNoRestart()
    {
        // ci/installer-e2e.ps1 checks the setup's and the uninstaller's logs for these lines, the setup's exit code and
        // the folder the setup sets its files aside in: each must be what the script writes (its string literals joined,
        // as the log has them). Its runs that finish at the next restart are not told /NORESTART, so a restart asked for by
        // mistake, Inno Setup's own for an unfinished earlier install among them, would restart the runner and fail the job.
        string e2e = Source("ci", "installer-e2e.ps1");
        string code = Joined(Code());
        // Each text this test finds in the script is kept, so that each check that holds one is held to its marker below.
        var pinned = new List<string>();
        void InE2e(string text) { Assert.Contains(text, e2e); pinned.Add(text); }
        void InLf(string text) { Assert.Contains(text, e2e.Replace("\r\n", "\n")); pinned.Add(text); }
        foreach (var line in new[]
                 {
                     "In use, so it cannot be replaced or removed now: ",
                     "Windows deletes it at its next restart: ",
                     "Part of PoolWatch may still run after its cleanup, so the cleanup is to run once more",
                     "PoolWatch still runs from the install folder: ",
                     "In use, so Setup writes its uninstaller in a folder of its own: ",
                     "An earlier setup or uninstall of PoolWatch finishes when Windows restarts, so this one does too, after it.",
                     "Windows puts this version of PoolWatch in place the next time it restarts",
                     "is not there, or the launcher beside it is of another version, so PoolWatch put nothing back and the uninstaller has removed nothing",
                     "finishes when Windows next restarts, so its files are not in place yet: PoolWatch put nothing back and the uninstaller has removed nothing",
                     " (in use by ",
                     "PoolWatch is not installed: an earlier uninstall removed it",
                     "PoolWatch does not start: no file that starts it is in place",
                     "the version you had stays in place",
                     "Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not be kept from running until then",
                 })
        {
            Assert.Contains(line, code);
            Assert.True(e2e.Contains(line, StringComparison.Ordinal) || e2e.Contains(line.Replace(".", @"\."), StringComparison.Ordinal), line); // as it is, or as a regular expression
            pinned.Add(e2e.Contains(line, StringComparison.Ordinal) ? line : line.Replace(".", @"\."));
        }
        // The words of what starts PoolWatch until the restart (UntilRestart) follow these, which the test looks for with them.
        Assert.Contains("Text := Text + '. Until that restart ' + UntilRestart(App) + '.';", Code());
        Assert.Contains("else if OldUninstaller or (Refused = 0) then Text := Text + ' Until that restart PoolWatch''s uninstaller removes nothing.';", Code());
        // 1.0.0-beta.1's uninstaller held by its messages file, by its program, and by none of them: the lines the script
        // logs about them, and the box's words when its program is set aside.
        Assert.Contains("Log('Set aside ' + Path + ' until the next restart, when Windows puts it back.');", Whole("HoldUntilRestart"));
        InE2e("[regex]::Escape(\"Set aside $beta1Msg until the next restart, when Windows puts it back\")");
        InE2e("[regex]::Escape(\"Set aside $beta1Exe until the next restart, when Windows puts it back\")");
        Assert.Contains("Log('None of ' + Path + ', ' + MsgFile + ' and ' + ExeFile + ' could be set aside until the next restart, ' +", Code());
        InE2e("[regex]::Escape(\"None of $beta1Dat, $beta1Msg and $beta1Exe could be set aside until the next restart\")");
        Assert.Contains("' Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if ' + 'Windows offers to remove PoolWatch " +
                        "from its list of apps before then, answer No.'", Code());
        InE2e("Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if Windows offers to remove PoolWatch from its " +
                        "list of apps before then, answer No\\.'");
        InE2e("-match 'Until that restart PoolWatch does not start: no file that starts it is in place\\. Until that restart PoolWatch''s uninstaller removes nothing'");
        Assert.Contains("Meanwhile := 'until then ' + UntilRestart(ExpandConstant('{app}'));", Code());
        Assert.Contains("else Result := 'Windows puts this version of PoolWatch in place the next time it restarts; ' + Meanwhile + '.", Code());
        InE2e("-match 'Windows puts this version of PoolWatch in place the next time it restarts; until then PoolWatch is not installed: an earlier uninstall removed it'");
        InE2e("-match 'Windows puts this version of PoolWatch in place the next time it restarts; until then the version you had stays in place\\.'");
        // The box of a setup that did not finish names the first MaxShown files in use, each with what has it open, on lines
        // of their own after these words (Inno Setup's log indents each line after the first); the test finds the held
        // file on any of the first ten lines, whichever files sort before it.
        Assert.Contains("', names every one):' + HeldList + MoreLine(HeldCount)", Code());
        Assert.Contains("List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown);", Body("AddShown"));
        InE2e("('names every one\\):(\\r?\\n[^\\r\\n]*){0," + Number("MaxShown") + "}?\\r?\\n[^\\r\\n]{0,40}?- ' + [regex]::Escape('app\\PoolWatch.exe (in use by ')");
        // The lines the test builds around a path: what the script writes before and after it.
        Assert.Contains("'An earlier uninstall gave Windows ' + Folder + ' to delete at its next restart", Code());
        InE2e("\"An earlier uninstall gave Windows $(Split-Path $exe) to delete at its next restart\"");
        Assert.Contains("Log('Put back ' + Target + '.')", Code());
        InE2e("\"Put back $exe.\"");
        Assert.Contains("Holders := HoldersOf(Path); AddShown(HeldList, HeldCount, Rel + Name + Holders); HeldAll := HeldAll + #13#10 + Rel + Name + Holders; " +
                        "if Logged then Log('In use, so it cannot be replaced or removed now: ' + Path + Holders);", Code());
        Assert.Contains("Result := Result + Format('%s, process %d, run by %s', [Name, Id, AccountOf(Id)]);", Code());
        InE2e(@"""[^\r\n]*process $($holder.Id), run by """);
        InE2e($"$p.ExitCode -eq {Number("ExitFinishesAtRestart")}");
        InE2e($"$code -eq {Number("ExitFinishesAtRestart")}");
        InE2e($@"$app\{Constant("StageFolder")}\*\app\PoolWatch.exe");
        InE2e($@"$app\{Constant("UninstallFolder")}\*\unins000.exe");
        InE2e($"-Filter '{Constant("StageFolder")}*'"); // the stage folder and the files that keep folders (KeepName)
        Assert.StartsWith(Constant("StageFolder"), Constant("KeepName"));
        // The pass that upgrades 1.0.0-beta.1 looks for the line StopBeforeLauncher logs.
        Assert.Contains("The installed PoolWatch, made before the launcher, was asked to exit through its exit signal.", Body("SetOldExit"));
        InE2e(@"-match 'The installed PoolWatch, made before the launcher, was asked to exit through its exit signal\.'");
        InE2e($"(Join-Path $app '{Constant("StageFolder")}')");
        var finishing = Regex.Match(e2e, @"function Assert-FinishesAtRestart[^\n]*\n[^\n]*Start-Process \$Setup -ArgumentList \(@\(([^)]*)\)");
        Assert.True(finishing.Success, "Assert-FinishesAtRestart starts the setup with a switch list of its own");
        Assert.Equal("'/VERYSILENT', '/SUPPRESSMSGBOXES'", finishing.Groups[1].Value);
        var invoke = Regex.Match(e2e, @"function Invoke-Setup\(\[string\]\$name, \[string\[\]\]\$switches = @\(([^)]*)\)\)");
        Assert.True(invoke.Success, "Invoke-Setup starts the setup with a switch list of its own");
        Assert.Equal("'/VERYSILENT', '/SUPPRESSMSGBOXES'", invoke.Groups[1].Value);
        InE2e("Uninstall 'uninstall-3' @('/DELETEDATA') @('/VERYSILENT', '/SUPPRESSMSGBOXES')");
        // Inno Setup logs whether it would restart Windows, and when its own check found one of the setup's files in
        // Windows' list (the case that would ask for a restart); the test reads both.
        InE2e(@"Need to restart Windows\? No");
        // Every run that finishes at the restart: Assert-FinishesAtRestart's, the two with a file held, and the one before an
        // uninstall's restart.
        Assert.Equal(3, Regex.Matches(e2e, Regex.Escape("-notmatch 'Found pending rename or delete that matches one of our files'")).Count);
        pinned.Add("-notmatch 'Found pending rename or delete that matches one of our files'");
        // A second process holds a file of PoolWatch's without letting it be renamed or deleted (FileShare.Read only).
        InE2e("[System.IO.File]::Open('$path', 'Open', 'Read', 'Read')");
        // An upgrade in place leaves no old file set aside (SetAsideIn names them <file>.<run>.old).
        Assert.Contains("Aside := Path + '.' + RunStamp + '.old';", Body("SetAsideIn"));
        InE2e("-Filter '*.old'");
        // The failed upgrades: a deny rule on the Uninstall entry for that step only, removed again whatever happens. The
        // first must leave every file of the install folder as it was (the uninstaller's own among them) and no other,
        // and the setup must say it put back the uninstaller's program it had replaced.
        string lf = e2e.Replace("\r\n", "\n");
        InE2e(@"RegistryAccessRule('BUILTIN\Administrators', 'SetValue, Delete', 'Deny')");
        InLf("    Set-UninstallKeyRule $deny $true\n    try { return (Invoke-Setup $name) }\n    finally { Set-UninstallKeyRule $deny $false }");
        // The rule is set on the key through .NET in the 64-bit view (Set-Acl on the registry path said the key did not
        // exist), and taken out the same way.
        InLf("            if ($add) { $acl.AddAccessRule($rule) } else { [void]$acl.RemoveAccessRule($rule) }\n            $key.SetAccessControl($acl)");
        InE2e("[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)");
        Assert.DoesNotContain("Set-Acl", ScriptCode.PowerShell(e2e)); // in its code: its comment says why
        // Its functions write their items one by one, as its callers collect them with @(...): a list written whole
        // (return , $list) is collected as one item, so Windows' restart work read as none and its count as 1.
        Assert.DoesNotMatch(@"return\s*,", ScriptCode.PowerShell(e2e));
        InLf("    if ($null -eq $value) { return }\n    return @($value)\n}");
        // Each entry of Windows' list is read as the setup reads it (ListedPath): without the *1 or *2 that recent Windows
        // builds put first (before a move target's !), the !, a *1 or *2 after it, and the \??\ (with only the last
        // stripped, no entry of the install folder was found; with the ! stripped before the *1, every target kept its !).
        InE2e(@"return ($item -replace '^\*[12]', '' -replace '^!', '' -replace '^\*[12]', '' -replace '^\\\?\?\\', '')");
        Assert.Equal(2, Regex.Matches(e2e, Regex.Escape("$from = Get-ListedPath $ops[$i]")).Count);
        Assert.Equal(2, Regex.Matches(e2e, Regex.Escape("$to = Get-ListedPath $ops[$i + 1]")).Count);
        Assert.Contains("if (CharAt(Result, 1) = '*') and ((CharAt(Result, 2) = '1') or (CharAt(Result, 2) = '2')) then", Whole("ListedPath"));
        // The test does the restart's work itself (Complete-RestartWork), then takes every entry for the install folder
        // out of Windows' list, done or not, so a check after it that the list holds no work could not fail. So it
        // writes out, one by one, each entry it could not carry out (one that failed, or a move whose file is not
        // there), but not a folder left because it is not empty, which Windows leaves too; each call keeps them, and
        // the check after it, the next to read Windows' list, fails on any.
        Assert.Equal(OneLine("""
            function Complete-RestartWork {
                foreach ($name in 'PendingFileRenameOperations', 'PendingFileRenameOperations2') {
                    $ops = @(Get-RestartValue $name)
                    if ($ops.Count -eq 0) { continue }
                    $keep = [System.Collections.Generic.List[string]]::new()
                    for ($i = 0; $i + 1 -lt $ops.Count; $i += 2) {
                        $from = Get-ListedPath $ops[$i]
                        $to = Get-ListedPath $ops[$i + 1]
                        if (-not $from.StartsWith($app, [StringComparison]::OrdinalIgnoreCase)) { $keep.Add($ops[$i]); $keep.Add($ops[$i + 1]); continue }
                        try {
                            if ($to -eq '') {
                                if (Test-Path -LiteralPath $from -PathType Container) {
                                    if (@(Get-ChildItem -LiteralPath $from -Force).Count -eq 0) { [System.IO.Directory]::Delete($from) }
                                    else { Write-Host "  at the restart: $from is not empty, so it stays, as Windows leaves it" }
                                }
                                elseif (Test-Path -LiteralPath $from) { [System.IO.File]::Delete($from) }
                            }
                            elseif (Test-Path -LiteralPath $from) { [System.IO.File]::Move($from, $to, $true) }
                            else { throw "$from is not there" }
                        }
                        catch {
                            Write-Host "  at the restart: $from -> '$to' was not done ($($_.Exception.Message)), as Windows would leave it"
                            "$from -> '$to'"
                        }
                    }
                    if ($keep.Count -eq 0) { Remove-ItemProperty -LiteralPath $sessionManager -Name $name }
                    else { Set-ItemProperty -LiteralPath $sessionManager -Name $name -Value $keep.ToArray() -Type MultiString }
                }
            }
            """), OneLine(ScriptCode.PowerShellFunction(e2e, "Complete-RestartWork").Value));
        string psLf = ScriptCode.PowerShell(e2e).Replace("\r\n", "\n");
        const string RestartWorkCheck = "Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) ";
        Assert.Equal(7, Regex.Matches(psLf, @"(?<!function )Complete-RestartWork\b").Count);
        Assert.Equal(7, Regex.Matches(psLf, Regex.Escape("$notDone = @(Complete-RestartWork)")).Count);
        Assert.Equal(7, Regex.Matches(psLf, Regex.Escape("$notDone = @(Complete-RestartWork)") + @"\n(?:(?![^\n]*(?:Get-RestartWork|Complete-RestartWork))[^\n]*\n)*?[ ]*" +
                                            Regex.Escape(RestartWorkCheck)).Count);
        pinned.Add(RestartWorkCheck);
        // A process the test starts and later ends or checks is kept as the process its start gave, with a handle to
        // it held, never as its ID alone: Windows gives no other process that ID while the handle is open, so a holder
        // that has ended already (the setup ended it, or an earlier Stop-Held did) is seen to have ended, and no other
        // program is ended in its place. StartSuspended takes that handle while CreateProcessW's own is still open.
        Assert.Equal(OneLine("""
            function Stop-Held([System.Diagnostics.Process]$process) {
                if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(30000) }
            }
            """), OneLine(ScriptCode.PowerShellFunction(e2e, "Stop-Held").Value));
        InE2e("public static Process StartSuspended(string exe, string arguments)");
        InLf("        CloseHandle(information.thread);\n        try\n        {\n            var process = Process.GetProcessById(information.processId);\n" +
             "            IntPtr held = process.Handle;\n            return process;\n        }\n        finally { CloseHandle(information.process); }\n");
        Assert.Equal(2, Regex.Matches(lf, Regex.Escape("-WindowStyle Hidden -PassThru\n    $null = $p.Handle   # keeps its ID from being another process's once it has " +
                                                       "ended\n")).Count); // Start-Holder's and Start-Watcher's
        Assert.Equal(2, Regex.Matches(lf, @"\n    return \$p\n\}\n").Count);
        Assert.Contains("function Assert-FinishesAtRestart([string]$name, [System.Diagnostics.Process]$process) {", e2e);
        Assert.Contains("-and $helper.HasExited) \"a token-leak test helper started through the short spelling was ended", e2e);
        foreach (string byId in new[] { "Get-Process -Id", "Stop-Process -Id $id", "Wait-Process", "return $p.Id", "return information.processId" })
            Assert.DoesNotContain(byId, ScriptCode.PowerShell(e2e));
        // common.ps1 sets $ErrorActionPreference to Stop, so a check, or a value one uses, that reads a file an earlier
        // failure left missing would end the run there, hiding every later result and skipping Complete-Assertions.
        // Each check's read of a file's version, hash or bytes follows a Test-Path of that file in the same -and chain,
        // and the values the checks use read nothing when the file is not there.
        Assert.Contains("$hash = if ($staged -and (Test-Path -LiteralPath $staged)) { (Get-FileHash -LiteralPath $staged).Hash } else { '' }", e2e);
        Assert.Contains("$oldVersion = (Get-Item $second -ErrorAction SilentlyContinue).VersionInfo.ProductVersion", e2e);
        Assert.Contains("$saved = @(Get-Content (Join-Path $data 'service-changes.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)", e2e);
        Assert.Contains("$tail = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $running.Skip)", e2e);
        var checks = new List<string>();
        void Checks(string code)
        {
            foreach (string statement in ScriptCode.Statements(code))
            {
                if (statement.StartsWith("Assert-True ", StringComparison.Ordinal)) checks.Add(OneLine(statement));
                foreach (var block in ScriptCode.Blocks(statement)) Checks(block.Inside);
            }
        }
        Checks(ScriptCode.PowerShell(e2e));
        var reads = checks.SelectMany(c => Regex.Matches(c, @"\((?:Get-Item|Get-FileHash -LiteralPath) (\$\w+)\)|ReadAllBytes\((\$\w+)\)").Select(m => (Check: c, Read: m))).ToList();
        Assert.Equal(13, reads.Count); // the reader finds them: three pairs of versions, the zip step's three, two exes' bytes and two hashes
        var unguarded = reads.Where(r =>
        {
            string file = r.Read.Groups[1].Success ? r.Read.Groups[1].Value : r.Read.Groups[2].Value, before = r.Check[..r.Read.Index];
            return !before.Contains($"(Test-Path -LiteralPath {file}) -and ", StringComparison.Ordinal) && !before.Contains($"(Test-Path {file}) -and ", StringComparison.Ordinal);
        }).Select(r => r.Check).Distinct().ToList();
        Assert.True(unguarded.Count == 0, "checks that read a file no Test-Path before it guards: " + string.Join(Environment.NewLine, unguarded));
        InE2e("foreach ($f in @(Get-ChildItem -LiteralPath $app -Recurse -Force -File -ErrorAction SilentlyContinue)) {");
        InLf("$changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })\n" +
                        "Assert-True ($changed.Count -eq 0) \"the failed upgrade left every file of the version that was there as it was, and no other ($($changed -join ', '))\"");
        Assert.Equal(5, Regex.Matches(lf, Regex.Escape("Assert-True ($changed.Count -eq 0) \"")).Count); // and the upgrades from 1.0.0-beta.1 that fail with a file held (one line for the two with its uninstaller held)
        pinned.Add("Assert-True ($changed.Count -eq 0) \"");
        Assert.Contains("Log('Put back ' + Path + ' as it was before the setup.')", Whole("PutUninstallerBack"));
        Assert.Contains("Path := App + '\\' + UninstallerBefore[I];", Whole("PutUninstallerBack"));
        InE2e("[regex]::Escape(\"Put back $(Join-Path $app 'unins000.exe') as it was before the setup.\")");
        // A new file held open: the setup says so, and no old file goes back before the restart (each then, the launcher last).
        Assert.Contains("', so it stays until the next restart, and no old file goes back before then.'", Joined(Code()));
        InE2e("[^\\r\\n]*so it stays until the next restart, and no old file goes back before then\\.");
        InE2e("$back[-1].To -ieq $launcher");
        InE2e("-not (Test-Path -LiteralPath $launcher) -and -not (Test-Path -LiteralPath $exe)");
        // A later failure: the entry is written back, every value as it was.
        Assert.Contains("Log('Put back the Apps and Features entry as it was before the setup.')", Code());
        InE2e("-match 'Put back the Apps and Features entry as it was before the setup\\.'");
        InE2e("Assert-True ($entryBefore.Count -gt 0 -and $entryChanged.Count -eq 0)");
        // An old file set aside comes into use without being shared at all: put back under a second name of the same file,
        // the update sent to the restart with that file named.
        Assert.Contains(" came into use while Setup was setting the old files aside", Code());
        InE2e("' \\(in use by [^\\r\\n]*came into use while Setup was setting the old files aside'");
        Assert.Contains("Log('Put back ' + Target + How + Aside + ' is in use' + HoldersOf(Aside) + '.');", Code());
        Assert.Contains("else if CreateHardLinkW(Target, Aside, 0) then How := ' under a second name of the same file: '", Code());
        InE2e("[regex]::Escape(\"Put back $original under a second name of the same file: $watched is in use\")");
        // The watcher: it opens the file for reading and shares it only as the step says (Read: no rename or delete), woken
        // by an oplock on the install folder, which Windows breaks as an entry there is renamed (a loop that sleeps
        // between looks woke after the setup had looked at the files it set aside again: 15 ms against some 10).
        InE2e("FileStream f = new FileStream(target, FileMode.Open, FileAccess.Read, share);");
        InE2e("input.RequestedOplockLevel = OPLOCK_LEVEL_CACHE_READ | OPLOCK_LEVEL_CACHE_HANDLE;");
        InE2e("bool ok = DeviceIoControl(h, FSCTL_REQUEST_OPLOCK, ref input, Marshal.SizeOf(typeof(RequestInput)), output, 64, IntPtr.Zero, ov);");
        InE2e("`$f = [InstallFolderWatch]::Hold('$app', '$trigger', '$path', '$share', 600000, '$ready', '$heldFile')");
        InE2e("Start-Watcher 'upgrade-failed-held' 'LICENSE.*.old' $license 'Read'");
        InE2e("Start-Watcher 'upgrade-failed-late' '*.old' $dat 'ReadWrite'");
        InE2e("Start-Watcher 'upgrade-aside-held' '*.old' '' 'None'");
        // Apps and Features starts the uninstaller with /LOG, so it writes a log (UninstallLogging): the entry's command is
        // its quoted path then /LOG, which the test reads.
        InE2e("if ($command -match '^\"([^\"]+)\"') { return $Matches[1] }");
        InE2e("-eq \"`\"$(Join-Path $app 'unins000.exe')`\" /LOG\"");
        // Each check of the script that holds a text found above (each statement outside brackets that starts with
        // Assert-True and a space, as ChecksWithoutAMarker reads them) prints its CHECK-RAN marker, so CI shows that it
        // ran: in its own block, as the statement right after it or right after the last check of its run, or right
        // after the try statement with no catch whose body holds it (CheckMarkerTests holds each marker to
        // ci/expected-checks.txt).
        var unmarked = ChecksWithoutAMarker(Source("ci", "installer-e2e.ps1"), pinned);
        Assert.True(unmarked.Count == 0, "pinned checks whose CHECK-RAN marker is not where it must be: " + string.Join(Environment.NewLine, unmarked));
        Assert.True(pinned.Count > 40, "too few texts are held to their checks");
    }

    /// <summary>
    /// The checks of a CI script that hold one of the <paramref name="pinned"/> texts and whose CHECK-RAN marker is
    /// not where it must be, each with its white space made one space. The script is read without its comments,
    /// statement by statement (<see cref="ScriptCode.Statements"/>): the statements of its top level, and those of
    /// each block of a statement read so, at any depth, each block found by its braces outside brackets
    /// (<see cref="ScriptCode.Blocks"/>, such as an if's, a loop's, a try's, a function's or a script block given to a
    /// command), so its indentation does not count and a one-line block's statements are read too
    /// ("foreach (...) { Assert-True ... }"). What is inside brackets is not read: a statement in <c>$( )</c>,
    /// <c>@( )</c> or <c>( )</c>, or in a block inside them, is not found. A check is a statement read so that starts
    /// with Assert-True and a space; one with a tab or a bracket right after Assert-True is not a check. A pinned text
    /// is read with its white space made one space and its ends trimmed (<see cref="OneLine"/>), so a text that ends
    /// in Assert-True and white space is read as one ending in Assert-True. The check holds a pinned text when it
    /// holds one so read in which no Assert-True is followed by a space; or, for a text in which one is, when one of
    /// the text's parts from such an Assert-True up to the next (or its end) is a part of the check (the whole check
    /// too), or is the check followed by more: the check, then a space, a semicolon or a closing brace, then anything
    /// (a part shorter than "Assert-True (x)", and a text of fewer than eight characters, holds none). Its marker must
    /// be the statement right after it, or right after the last check of the run of checks it is in (statements that
    /// are checks, each straight after the one before), in the check's own block; or, for a check in the body of a try
    /// statement with no catch, the statement right after that whole try statement, its finally included, in the block
    /// the try is in (the statement after a try with no catch runs only when its body ran to its end). A marker
    /// anywhere else, before the check, after another statement or inside another block, does not count.
    /// </summary>
    internal static List<string> ChecksWithoutAMarker(string script, IReadOnlyList<string> pinned)
    {
        var whole = new List<string>();
        var parts = new List<string>();
        foreach (string text in pinned.Select(OneLine))
        {
            var starts = Regex.Matches(text, "Assert-True ").Select(m => m.Index).ToList();
            if (starts.Count == 0) whole.Add(text);
            for (int k = 0; k < starts.Count; k++) parts.Add(text[starts[k]..(k + 1 < starts.Count ? starts[k + 1] : text.Length)].Trim());
        }
        whole = whole.Where(p => p.Length >= 8).Distinct().ToList();
        parts = parts.Where(p => p.Length >= "Assert-True (x)".Length).Distinct().ToList();
        bool Pinned(string check) => whole.Any(p => check.Contains(p, StringComparison.Ordinal)) ||
            parts.Any(p => check.Contains(p, StringComparison.Ordinal) ||
                p.Length > check.Length && p.StartsWith(check, StringComparison.Ordinal) && p[check.Length] is ' ' or ';' or '}');
        static bool IsCheck(string statement) => statement.StartsWith("Assert-True ", StringComparison.Ordinal);
        static bool IsMarker(string statement) => Regex.IsMatch(OneLine(statement), @"^Write-Host 'CHECK-RAN: [a-z0-9]+(?:-[a-z0-9]+)*'$");
        var unmarked = new List<string>();
        // The statements of a block; around: the try statement whose body it is (its block's statements and its place
        // there) when that try has no catch.
        void Walk(string code, (IReadOnlyList<string> Statements, int At)? around)
        {
            var statements = ScriptCode.Statements(code);
            for (int i = 0; i < statements.Count; i++)
            {
                string statement = statements[i];
                if (IsCheck(statement) && Pinned(OneLine(statement)))
                {
                    int next = i + 1;
                    while (next < statements.Count && IsCheck(statements[next])) next++;
                    bool marked = next < statements.Count && IsMarker(statements[next]) ||
                        around is { } t && t.At + 1 < t.Statements.Count && IsMarker(t.Statements[t.At + 1]);
                    if (!marked) unmarked.Add(OneLine(statement));
                }
                var blocks = ScriptCode.Blocks(statement);
                bool tryWithoutCatch = blocks.Count > 0 && blocks[0].Before.Trim() == "try" &&
                    !blocks.Skip(1).Any(b => Regex.IsMatch(b.Before.Trim(), @"^catch\b"));
                for (int b = 0; b < blocks.Count; b++) Walk(blocks[b].Inside, b == 0 && tryWithoutCatch ? (statements, i) : null);
            }
        }
        Walk(ScriptCode.PowerShell(script), null);
        return unmarked;
    }

    [Fact]
    public void ACheckWithoutAMarkerInItsBlockIsFound()
    {
        string[] pinned = ["a pinned check"];
        // The statement right after the check, or right after the last check of its run, with comments and blank lines
        // between.
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\n\n# the next\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\nAssert-True ($b) 'other'\nAssert-True ($c) 'more'\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) ('a pinned check' +\n    ' of two lines')\nAssert-True (@($d | Where-Object {\n    $_.x\n}).Count -eq 0) 'other'\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("if ($c) {\n    Assert-True ($a) 'a pinned check'\n    Write-Host 'CHECK-RAN: x'\n}", pinned));
        // A marker after another statement, before the check (the shape of a marker moved above the last checks of a
        // run), or inside a block of the next statement does not count.
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\n$b = 1\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\nAssert-True ($b) 'other'\n$b = 1\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($b) 'other'\nWrite-Host 'CHECK-RAN: x'\nAssert-True ($a) 'a pinned check'\nAssert-True ($c) 'more'\nComplete-RestartWork\nAssert-True ($d) 'last'\nWrite-Host 'CHECK-RAN: y'", pinned));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\nif ($b) { Write-Host 'CHECK-RAN: x' }", pinned));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\nif ($b) {\n    Write-Host 'CHECK-RAN: x'\n}", pinned));
        Assert.Single(ChecksWithoutAMarker("if ($c) {\n    Assert-True ($a) 'a pinned check'\n}\nWrite-Host 'CHECK-RAN: x'", pinned)); // after its block
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'\n# Write-Host 'CHECK-RAN: x'", pinned)); // a comment
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'a pinned check'", pinned));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) 'other'", pinned)); // not pinned
        // Blocks are read by their braces, not by indentation: a marker indented more is still the next statement of the
        // block; a closing brace or a catch indented as its block's statements still ends that block or holds the catch.
        Assert.Empty(ChecksWithoutAMarker("foreach ($x in 1, 2) {\n    Assert-True ($a) 'a pinned check'\n        Write-Host 'CHECK-RAN: x'\n}", pinned));
        Assert.Single(ChecksWithoutAMarker("if ($c) {\nAssert-True ($a) 'a pinned check'\n}\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("if ($c) {\n    Assert-True ($a) 'a pinned check'\n    }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n}\n    catch { $e = $_ }\nWrite-Host 'CHECK-RAN: x'", pinned));
        // A check in a block on one line, or in a script block a command runs, is found like any other.
        Assert.Single(ChecksWithoutAMarker("foreach ($x in 1, 2) { Assert-True ($a) 'a pinned check' }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("foreach ($x in 1, 2) { Assert-True ($a) 'a pinned check'; Write-Host 'CHECK-RAN: x' }", pinned));
        Assert.Single(ChecksWithoutAMarker("if ($c) { $b = 1 } else { Assert-True ($a) 'a pinned check' }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("$xs | ForEach-Object {\n    Assert-True ($a) 'a pinned check'\n}\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("$b = 1; Assert-True ($a) 'a pinned check'; $c = 1; Write-Host 'CHECK-RAN: x'", pinned));
        // What is inside brackets is not read: a check in @( ), $( ) or ( ) is not found.
        Assert.Empty(ChecksWithoutAMarker("$r = @(\n    Assert-True ($a) 'a pinned check'\n)", pinned));
        Assert.Empty(ChecksWithoutAMarker("$r = $(Assert-True ($a) 'a pinned check')", pinned));
        Assert.Empty(ChecksWithoutAMarker("(Assert-True ($a) 'a pinned check')", pinned));
        // A statement with a tab or a bracket right after Assert-True is not a check.
        Assert.Empty(ChecksWithoutAMarker("Assert-True\t($a) 'a pinned check'", pinned));
        Assert.Empty(ChecksWithoutAMarker("Assert-True($a) 'a pinned check'", pinned));
        // A check in a try's body: marked by the statement right after the whole try statement (its finally included),
        // in the block the try is in, but no further out, not after another statement, and not when the try has a
        // catch.
        Assert.Empty(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n} finally { Stop-Held $h }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n    $b = 1\n}\nfinally {\n    Stop-Held $h\n}\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Empty(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n    Write-Host 'CHECK-RAN: x'\n    $b = 1\n} finally { Stop-Held $h }", pinned)); // in its block
        Assert.Single(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n    $b = 1\n    Write-Host 'CHECK-RAN: x'\n} finally { Stop-Held $h }\n$c = 1", pinned));
        Assert.Single(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n} finally { Stop-Held $h }\n$c = 1\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n} catch { $e = $_ }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("try {\n    Assert-True ($a) 'a pinned check'\n}\ncatch {\n    $e = $_\n} finally { Stop-Held $h }\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("foreach ($x in 1) {\n    try {\n        Assert-True ($a) 'a pinned check'\n    } finally { Stop-Held $h }\n}\nWrite-Host 'CHECK-RAN: x'", pinned));
        Assert.Single(ChecksWithoutAMarker("if ($c) {\n    Assert-True ($a) 'a pinned check'\n} else { $b = 1 }\nWrite-Host 'CHECK-RAN: x'", pinned)); // not a try
        // A text in which, with white space made one space and its ends trimmed, Assert-True is followed by a space holds
        // its parts from each such Assert-True to the next: a check that a part is, that a part holds followed by more,
        // or that holds a part (the start of a check); not what comes before the first. A text that ends in Assert-True
        // and white space is trimmed, so it is read whole.
        Assert.Single(ChecksWithoutAMarker("Assert-True ($x) 'a $b = 1 Assert-True'", ["$b = 1\nAssert-True "]));
        Assert.Single(ChecksWithoutAMarker("$changed = 1\nAssert-True ($changed) 'a pinned check'", ["$changed = 1\nAssert-True ($changed) 'a pinned check'"]));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($changed -eq 1) 'other'", ["$changed = 1\nAssert-True ($changed) 'a pinned check'"]));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'one'\n$b = 1", ["Assert-True ($a) 'one'\n$b = 1"]));
        // The check followed by more after a semicolon or a closing brace, as after a space.
        Assert.Single(ChecksWithoutAMarker("Assert-True ($a) 'one'; $b = 1", ["Assert-True ($a) 'one'; $b = 1"]));
        Assert.Equal(2, ChecksWithoutAMarker("Assert-True ($a) 'one'; Assert-True ($b) 'two'", ["Assert-True ($a) 'one'; Assert-True ($b) 'two'"]).Count);
        Assert.Single(ChecksWithoutAMarker("if ($c) { Assert-True ($a) 'one'}", ["if ($c) { Assert-True ($a) 'one'}"]));
        Assert.Single(ChecksWithoutAMarker("if ($c) { Assert-True ($a) 'one' }", ["if ($c) { Assert-True ($a) 'one' }"]));
        Assert.Single(ChecksWithoutAMarker("Install 'x'\nAssert-True ($a) 'one'\nNext-Step 'y'", ["Install 'x' Assert-True ($a) 'one' Next-Step 'y'"]));
        Assert.Single(ChecksWithoutAMarker("$c = 1\nAssert-True ($code -eq 0) 'one'\n$d = 1", ["$c = 1 Assert-True ($code -eq 0) "]));
        Assert.Equal(2, ChecksWithoutAMarker("Assert-True ($a) 'one'\n$b = 1\nAssert-True ($c) 'two'\n$d = 1", ["Assert-True ($a) 'one'\n$b = 1\nAssert-True ($c) 'two'"]).Count);
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($a) 'one more'\n$b = 1", ["Assert-True ($a) 'one'\n$b = 1"])); // another check
        // A part shorter than "Assert-True (x)" (15 characters), or a text of fewer than eight characters, holds no
        // check; a part of 15 characters and a text of eight do.
        Assert.Empty(ChecksWithoutAMarker("Assert-True $a 'one'\n$b = 1", ["$b = 1\nAssert-True $a"]));
        Assert.Empty(ChecksWithoutAMarker("Assert-True ($b -eq 100) 'one'", ["-eq 100"]));
        Assert.Single(ChecksWithoutAMarker("Assert-True $ab 'one'", ["Assert-True $ab"]));
        Assert.Single(ChecksWithoutAMarker("Assert-True ($b -eq 1000) 'one'", ["-eq 1000"]));
    }

    [Fact]
    public void TheInstallerTestsFailedAndHeldUpgradesCheckWhatTheySay()
    {
        // These steps of ci/installer-e2e.ps1 are held whole here, from the first command to the last check:
        // upgrade-beta1-failed-held, upgrade-beta1-msg-aside, upgrade-beta1-exe-aside and upgrade-beta1-uninstaller-held (an
        // upgrade from 1.0.0-beta.1 that fails while another program holds a new file, the next three while a third
        // process also holds that version's uninstaller record, in the last two its messages file too, and in the last its
        // program as well), upgrade-failed-held and upgrade-failed-late (an upgrade that fails part way, the first while
        // another program holds a new file) and upgrade-aside-held (an old file set aside comes into use). Each is held
        // with what it holds, how it runs the setup, and each thing it asserts (the exit code, the log lines, the files and
        // the Uninstall entry before and after, Windows' list of work for the restart, the uninstaller started before the
        // restart, which must stop at its start within the wait, with exit code 1 and no log, and the install after that
        // work). A changed or weakened check in them fails here; CI runs them. The script's other upgrades in which
        // another process holds one of PoolWatch's files (upgrade-held, upgrade-junction, upgrade-file-held,
        // upgrade-file-held-again, upgrade-uninstaller-held) are not held whole here:
        // TheInstallerTestLooksForWhatTheScriptWritesAndRunsWithoutNoRestart holds the log lines they look for and the
        // switches they run the setup with. Nor are the script's steps in which a process runs one of PoolWatch's files
        // while a setup or an uninstall runs (upgrade-helper, which must end it, and uninstall-3, which leaves the exe it
        // runs to Windows to delete at the restart), whose lines that test does not all hold.
        string lf = Source("ci", "installer-e2e.ps1").Replace("\r\n", "\n");
        var steps = new List<string>();
        void Step(string text) { Assert.Contains(text, lf); steps.Add(text); }
        Step("""
            $beta1Files = Get-ProgramFiles
            $beta1Dat = Join-Path $app 'unins000.dat'
            $holder = Start-Watcher 'upgrade-beta1-failed-held' 'LICENSE.*.old' $exe 'Read'
            try {
                $code = Invoke-SetupThatFails 'upgrade-beta1-failed-held'
                $failedLog = Get-UninstallLog 'upgrade-beta1-failed-held'
                Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-beta1-failed-held: the upgrade fails (exit $code)"
                Assert-True ((Get-Watched 'upgrade-beta1-failed-held') -ieq $exe) "upgrade-beta1-failed-held: the second process held the new app\PoolWatch.exe ($(Get-Watched 'upgrade-beta1-failed-held'))"
                Assert-True ($failedLog -match ([regex]::Escape("Another program has $exe open (in use by ") + "[^\r\n]*process $($holder.Id), run by [^\r\n]*so it stays until the next restart, and no old file goes back before then\.")) 'upgrade-beta1-failed-held: the setup named the new file in use and what has it open'
                Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Dat until the next restart, when Windows puts it back")) 'upgrade-beta1-failed-held: the setup set 1.0.0-beta.1''s uninstaller record aside until the restart'
                Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') 'upgrade-beta1-failed-held: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart'
                Assert-True ($failedLog -match ('names every one\):(\r?\n[^\r\n]*){0,10}?\r?\n[^\r\n]{0,40}?- ' + [regex]::Escape('app\PoolWatch.exe (in use by ') + "[^\r\n]*process $($holder.Id), run by ")) 'upgrade-beta1-failed-held: the setup''s words name the file in use and what has it open, on one of the lines of their list'
                Assert-True ((Test-Path -LiteralPath $exe) -and -not (Test-Path -LiteralPath $launcher) -and -not (Test-Path -LiteralPath $beta1Dat)) 'upgrade-beta1-failed-held: until the restart the install folder has no PoolWatch.exe and no uninstaller record'
                $work = @(Get-RestartWork)
                $back = @($work | Where-Object { $_.To -ne '' })
                $program = @($back | Where-Object { $_.To -notlike "$app\unins*" })
                Assert-True (@($work | Where-Object { $_.To -eq '' -and $_.From -ieq $exe }).Count -eq 1 -and $program.Count -ge 3 -and $program[-1].To -ieq $launcher -and @($back | Where-Object { $_.To -ieq $beta1Dat -and $_.From -like "$beta1Dat.*.old" }).Count -eq 1) "upgrade-beta1-failed-held: Windows deletes the new file and puts every old file back at the next restart, PoolWatch.exe last, and the uninstaller's record ($(@($back | ForEach-Object { $_.To }) -join ', '))"
                # 1.0.0-beta.1's uninstaller, started before that restart as a silent uninstall would start it: it finds no record,
                # stops at its start (Inno Setup's exit code 1, before the step that starts its log) and removes nothing.
                $filesHeld = Get-ProgramFiles
                $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\uninstall-beta1-before-restart.log`"") -PassThru
                $null = $p.Handle   # keeps the exit code readable after the process ends
                $exited = $p.WaitForExit(120000)
                if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
                $filesAfter = Get-ProgramFiles
                $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
                Assert-True ($exited -and $p.ExitCode -eq 1 -and -not (Test-Path -LiteralPath "$Out\uninstall-beta1-before-restart.log") -and $changed.Count -eq 0) "upgrade-beta1-failed-held: 1.0.0-beta.1's uninstaller, started before the restart, stopped at its start and removed nothing (exited: $exited, exit $(if ($p.HasExited) { $p.ExitCode } else { 'none' }); $($changed -join ', '))"
                Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) 'upgrade-beta1-failed-held: the Uninstall entry and the logon task are still there'
            } finally { Stop-Held $holder }
            Write-Host 'CHECK-RAN: upgrade-beta1-failed-held-before-restart'
            $notDone = @(Complete-RestartWork)
            $filesAfter = Get-ProgramFiles
            $changed = @($beta1Files.Keys | Where-Object { $filesAfter[$_] -ne $beta1Files[$_] }) + @($filesAfter.Keys | Where-Object { -not $beta1Files.ContainsKey($_) })
            Assert-True ($changed.Count -eq 0) "upgrade-beta1-failed-held: after the restart's work every file of 1.0.0-beta.1 is back as it was, its uninstaller's record included, and no other ($($changed -join ', '))"
            Assert-True (-not (Test-Path -LiteralPath (Split-Path $exe))) 'upgrade-beta1-failed-held: after the restart''s work the app folder the setup made is gone'
            Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-beta1-failed-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
            Write-Host 'CHECK-RAN: upgrade-beta1-failed-held'
            """.Replace("\r\n", "\n"));
        Step("""
            $beta1Msg = Join-Path $app 'unins000.msg'
            $beta1Exe = Join-Path $app 'unins000.exe'
            Assert-True (Test-Path -LiteralPath $beta1Msg) '1.0.0-beta.1''s signed uninstaller has its messages in unins000.msg'
            Write-Host 'CHECK-RAN: upgrade-beta1-has-msg'
            foreach ($step in 'upgrade-beta1-msg-aside', 'upgrade-beta1-exe-aside', 'upgrade-beta1-uninstaller-held') {
                $holder = Start-Watcher $step 'LICENSE.*.old' $exe 'Read'
                $datHolder = Start-Holder $beta1Dat
                $msgHolder = $null
                $exeHolder = $null
                if ($step -ne 'upgrade-beta1-msg-aside') { $msgHolder = Start-Holder $beta1Msg }
                if ($step -eq 'upgrade-beta1-uninstaller-held') { $exeHolder = Start-Holder $beta1Exe }
                try {
                    $code = Invoke-SetupThatFails $step
                    $failedLog = Get-UninstallLog $step
                    Assert-True ($code -ne 0 -and $code -ne 3010) "${step}: the upgrade fails (exit $code)"
                    Assert-True ((Get-Watched $step) -ieq $exe) "${step}: the second process held the new app\PoolWatch.exe ($(Get-Watched $step))"
                    Write-Host 'CHECK-RAN: upgrade-beta1-held-upgrade-fails'
                    $back = @((Get-RestartWork) | Where-Object { $_.To -ne '' })
                    if ($step -eq 'upgrade-beta1-msg-aside') {
                        Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Msg until the next restart, when Windows puts it back")) "${step}: the setup set 1.0.0-beta.1's uninstaller messages aside until the restart"
                        Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') "${step}: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart"
                        Assert-True ((Test-Path -LiteralPath $beta1Dat) -and -not (Test-Path -LiteralPath $beta1Msg) -and @($back | Where-Object { $_.To -ieq $beta1Msg -and $_.From -like "$beta1Msg.*.old" }).Count -eq 1) "${step}: until the restart unins000.msg is set aside, and Windows puts it back then"
                        Write-Host 'CHECK-RAN: upgrade-beta1-msg-aside-set-aside'
                        # The record's holder lets go. 1.0.0-beta.1's uninstaller, started before the restart as a silent uninstall
                        # would start it, finds no messages, stops at its start and removes nothing.
                        Stop-Held $datHolder
                        $datHolder = $null
                        $filesHeld = Get-ProgramFiles
                        $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\$step-uninstall.log`"") -PassThru
                        $null = $p.Handle   # keeps the exit code readable after the process ends
                        $exited = $p.WaitForExit(120000)
                        if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
                        $filesAfter = Get-ProgramFiles
                        $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
                        Assert-True ($exited -and $p.ExitCode -eq 1 -and -not (Test-Path -LiteralPath "$Out\$step-uninstall.log") -and $changed.Count -eq 0) "${step}: 1.0.0-beta.1's uninstaller, started before the restart, stopped at its start and removed nothing (exited: $exited, exit $(if ($p.HasExited) { $p.ExitCode } else { 'none' }); $($changed -join ', '))"
                        Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) "${step}: the Uninstall entry and the logon task are still there"
                        Write-Host 'CHECK-RAN: upgrade-beta1-msg-aside'
                    }
                    elseif ($step -eq 'upgrade-beta1-exe-aside') {
                        Assert-True ($failedLog -match [regex]::Escape("Set aside $beta1Exe until the next restart, when Windows puts it back")) "${step}: the setup set 1.0.0-beta.1's uninstaller program aside until the restart"
                        Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller is not there to start, so it removes nothing: if Windows offers to remove PoolWatch from its list of apps before then, answer No\.') "${step}: the setup said PoolWatch does not start and its uninstaller is not there to start until the restart"
                        Assert-True ((Test-Path -LiteralPath $beta1Dat) -and (Test-Path -LiteralPath $beta1Msg) -and -not (Test-Path -LiteralPath $beta1Exe) -and @($back | Where-Object { $_.To -ieq $beta1Exe -and $_.From -like "$beta1Exe.*.old" }).Count -eq 1) "${step}: until the restart unins000.exe is set aside, and Windows puts it back then"
                        Write-Host 'CHECK-RAN: upgrade-beta1-exe-aside-set-aside'
                        # The record's and the messages file's holders let go: the program the Uninstall entry starts is still
                        # not there.
                        Stop-Held $datHolder
                        $datHolder = $null
                        Stop-Held $msgHolder
                        $msgHolder = $null
                        Assert-True ((Get-Uninstaller) -ieq $beta1Exe -and -not (Test-Path -LiteralPath (Get-Uninstaller))) "${step}: until the restart the Uninstall entry starts nothing ($(Get-Uninstaller))"
                        Assert-True ((Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and $null -ne (Get-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\' -ErrorAction SilentlyContinue)) "${step}: the Uninstall entry and the logon task are still there"
                        Write-Host 'CHECK-RAN: upgrade-beta1-exe-aside'
                    }
                    else {
                        Assert-True ($failedLog -match [regex]::Escape("None of $beta1Dat, $beta1Msg and $beta1Exe could be set aside until the next restart")) "${step}: the setup could set none of the record, the messages and the program aside"
                        Assert-True ($failedLog -match 'Do not uninstall PoolWatch before that restart: the uninstaller of the version you had could not be kept from running until then') "${step}: the setup said not to uninstall before the restart"
                        Assert-True ((Test-Path -LiteralPath $beta1Dat) -and (Test-Path -LiteralPath $beta1Msg) -and (Test-Path -LiteralPath $beta1Exe) -and @($back | Where-Object { $_.To -like "$app\unins*" }).Count -eq 0) "${step}: the uninstaller's files stay as they are"
                        Write-Host 'CHECK-RAN: upgrade-beta1-uninstaller-held'
                    }
                } finally {
                    Stop-Held $holder
                    if ($datHolder) { Stop-Held $datHolder }
                    if ($msgHolder) { Stop-Held $msgHolder }
                    if ($exeHolder) { Stop-Held $exeHolder }
                }
                $notDone = @(Complete-RestartWork)
                $filesAfter = Get-ProgramFiles
                $changed = @($beta1Files.Keys | Where-Object { $filesAfter[$_] -ne $beta1Files[$_] }) + @($filesAfter.Keys | Where-Object { -not $beta1Files.ContainsKey($_) })
                Assert-True ($changed.Count -eq 0) "${step}: after the restart's work every file of 1.0.0-beta.1 is back as it was, and no other ($($changed -join ', '))"
                Assert-True (-not (Test-Path -LiteralPath (Split-Path $exe))) "${step}: after the restart's work the app folder the setup made is gone"
                Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "${step}: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
                Write-Host 'CHECK-RAN: upgrade-beta1-held-after-restart'
            }
            """.Replace("\r\n", "\n"));
        Step("""
            $license = Join-Path $app 'LICENSE'
            $filesBefore = Get-ProgramFiles
            $holder = Start-Watcher 'upgrade-failed-held' 'LICENSE.*.old' $license 'Read'
            try {
                $code = Invoke-SetupThatFails 'upgrade-failed-held'
                $failedLog = Get-UninstallLog 'upgrade-failed-held'
                Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-failed-held: the upgrade fails (exit $code)"
                Assert-True ((Get-Watched 'upgrade-failed-held') -ieq $license) "upgrade-failed-held: the second process held the new LICENSE ($(Get-Watched 'upgrade-failed-held'))"
                Assert-True ($failedLog -match ([regex]::Escape("Another program has $license open (in use by ") + "[^\r\n]*process $($holder.Id), run by [^\r\n]*so it stays until the next restart, and no old file goes back before then\.")) 'upgrade-failed-held: the setup named the new file in use and what has it open'
                Assert-True ((Test-Path -LiteralPath $license) -and -not (Test-Path -LiteralPath $launcher) -and -not (Test-Path -LiteralPath $exe)) 'upgrade-failed-held: until the restart the install folder has no launcher and no PoolWatch itself'
                Assert-True ($failedLog -match 'Until that restart PoolWatch does not start: no file that starts it is in place\. Until that restart PoolWatch''s uninstaller removes nothing') 'upgrade-failed-held: the setup said PoolWatch does not start and its uninstaller removes nothing until the restart'
                $back = @((Get-RestartWork) | Where-Object { $_.To -ne '' })
                Assert-True ($back.Count -ge 3 -and $back[-1].To -ieq $launcher -and @($back | Where-Object { $_.To -ieq $license -and $_.From -like "$license.*.old" }).Count -eq 1) "upgrade-failed-held: Windows puts every old file back at the next restart, LICENSE over the new one, the launcher last ($(@($back | ForEach-Object { $_.To }) -join ', '))"
                # This version's uninstaller, started before that restart as a silent uninstall would start it: the install folder
                # has no launcher of its program's version, so it starts no cleanup, removes nothing and says to restart first.
                $filesHeld = Get-ProgramFiles
                $p = Start-Process (Get-Uninstaller) -ArgumentList ($silent + "/LOG=`"$Out\uninstall-failed-held-before-restart.log`"") -Wait -PassThru
                $deadline = (Get-Date).AddMinutes(2)
                while ((Get-CimInstance Win32_Process -Filter "Name LIKE '[_]unins%'") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
                $filesAfter = Get-ProgramFiles
                $changed = @($filesHeld.Keys | Where-Object { $filesAfter[$_] -ne $filesHeld[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesHeld.ContainsKey($_) })
                Assert-True ($changed.Count -eq 0 -and (Get-UninstallLog 'uninstall-failed-held-before-restart') -match 'finishes when Windows next restarts, so its files are not in place yet: PoolWatch put nothing back and the uninstaller has removed nothing') "upgrade-failed-held: this version's uninstaller, started before the restart, removed nothing and said to restart Windows first (exit $($p.ExitCode); $($changed -join ', '))"
                Assert-True ((Test-Path $startMenu) -and (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch' -and (Get-StartMode $service) -eq 'Disabled') "upgrade-failed-held: after that uninstall the Start menu entry, the Uninstall entry and the change to $service are as they were"
            } finally { Stop-Held $holder }
            Write-Host 'CHECK-RAN: upgrade-failed-held-before-restart'
            $notDone = @(Complete-RestartWork)
            $filesAfter = Get-ProgramFiles
            $changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })
            Assert-True ($changed.Count -eq 0) "upgrade-failed-held: after the restart's work every file of the version that was there is back as it was, and no other ($($changed -join ', '))"
            Assert-True ((Test-Path $startMenu) -and (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayName -eq 'PoolWatch') 'upgrade-failed-held: the Start menu entry and the Uninstall entry are there'
            Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-failed-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
            Write-Host 'CHECK-RAN: upgrade-failed-held'
            """.Replace("\r\n", "\n"));
        Step("""
            $dat = Join-Path $app 'unins000.dat'
            $filesBefore = Get-ProgramFiles
            $entryBefore = Get-Entry
            $holder = Start-Watcher 'upgrade-failed-late' '*.old' $dat 'ReadWrite'
            try {
                $code = Invoke-Setup 'upgrade-failed-late'
                $failedLog = Get-UninstallLog 'upgrade-failed-late'
                Assert-True ($code -ne 0 -and $code -ne 3010) "upgrade-failed-late: an upgrade that cannot save the uninstaller's record fails (exit $code)"
                Assert-True ((Get-Watched 'upgrade-failed-late') -ieq $dat) "upgrade-failed-late: the second process held $dat ($(Get-Watched 'upgrade-failed-late'))"
                Assert-True ($failedLog -match 'Put back the Apps and Features entry as it was before the setup\.') 'upgrade-failed-late: the setup wrote the Uninstall entry back'
            } finally { Stop-Held $holder }
            Write-Host 'CHECK-RAN: upgrade-failed-late-entry-put-back'
            $entryAfter = Get-Entry
            $entryChanged = @($entryBefore.Keys | Where-Object { $entryAfter[$_] -ne $entryBefore[$_] }) + @($entryAfter.Keys | Where-Object { -not $entryBefore.ContainsKey($_) })
            Assert-True ($entryBefore.Count -gt 0 -and $entryChanged.Count -eq 0) "upgrade-failed-late: the Uninstall entry is as it was, every value ($($entryChanged -join ', '))"
            Write-Host 'CHECK-RAN: upgrade-failed-late-entry-as-it-was'
            $filesAfter = Get-ProgramFiles
            $changed = @($filesBefore.Keys | Where-Object { $filesAfter[$_] -ne $filesBefore[$_] }) + @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) })
            Assert-True ($changed.Count -eq 0) "upgrade-failed-late: every file of the version that was there is as it was, and no other ($($changed -join ', '))"
            Assert-True ((Test-Path $startMenu) -and (Test-Path -LiteralPath (Get-Uninstaller))) 'upgrade-failed-late: the Start menu entry and the uninstaller the entry names are there'
            Assert-True (@(Get-RestartWork).Count -eq 0) 'upgrade-failed-late: Windows has no work left in the install folder'
            Write-Host 'CHECK-RAN: upgrade-failed-late'
            """.Replace("\r\n", "\n"));
        Step("""
            $filesBefore = Get-ProgramFiles
            $holder = Start-Watcher 'upgrade-aside-held' '*.old' '' 'None'
            try {
                $code = Invoke-Setup 'upgrade-aside-held'
                $asideLog = Get-UninstallLog 'upgrade-aside-held'
                $watched = Get-Watched 'upgrade-aside-held'
                $original = $watched -replace '\.\d+\.old$', ''
                Assert-True ($code -eq 3010) "upgrade-aside-held: the setup finishes at the next restart (exit 3010, got $code)"
                Assert-True ($asideLog -match 'Need to restart Windows\? No') 'upgrade-aside-held: the setup asked for no restart'
                Assert-True ($watched -like "$app\*.old" -and $original -ne $watched) "upgrade-aside-held: the second process held an old file set aside ($watched)"
                Assert-True ($asideLog -match ([regex]::Escape($watched) + ' \(in use by [^\r\n]*came into use while Setup was setting the old files aside')) 'upgrade-aside-held: the setup said which file came into use while it set the files aside'
                Assert-True ($asideLog -match [regex]::Escape("Put back $original under a second name of the same file: $watched is in use")) "upgrade-aside-held: the setup put $original back under a second name of the same file"
                Assert-True ($asideLog -match ([regex]::Escape("In use, so it cannot be replaced or removed now: $watched (in use by ") + "[^\r\n]*process $($holder.Id), run by ")) 'upgrade-aside-held: the setup named the file in use and the process that has it open'
                Assert-True ((Test-Path -LiteralPath $original) -and (Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe)) "upgrade-aside-held: until the restart the version that was there is in place, $original among its files"
                Assert-True ($asideLog -match 'Windows puts this version of PoolWatch in place the next time it restarts; until then the version you had stays in place\.') 'upgrade-aside-held: the setup said the version that was there stays in place until the restart'
            } finally { Stop-Held $holder }
            Write-Host 'CHECK-RAN: upgrade-aside-held-before-restart'
            $filesAfter = Get-ProgramFiles
            # The uninstaller in the install folder itself goes now (the setup wrote its own in a folder of its own: SweepUninstallers).
            $changed = @($filesBefore.Keys | Where-Object { $_ -notlike "$app\unins*" -and $filesAfter[$_] -ne $filesBefore[$_] })
            $added = @($filesAfter.Keys | Where-Object { -not $filesBefore.ContainsKey($_) -and $_ -ine $watched -and $_ -notlike "$app\at-restart\*" -and $_ -notlike "$app\uninstall\*" })
            Assert-True ($changed.Count -eq 0 -and $added.Count -eq 0) "upgrade-aside-held: until the restart every file of the version that was there is as it was ($(@($changed + $added) -join ', '))"
            Write-Host 'CHECK-RAN: upgrade-aside-held-files-as-they-were'
            $notDone = @(Complete-RestartWork)
            Assert-True ((Test-Path -LiteralPath $launcher) -and (Test-Path -LiteralPath $exe) -and (Get-Item $launcher).VersionInfo.ProductVersion -eq (Get-Item $exe).VersionInfo.ProductVersion) 'upgrade-aside-held: after the restart''s work the launcher and PoolWatch itself are of one version'
            Write-Host 'CHECK-RAN: upgrade-aside-held-one-version'
            $aside = @(Get-ChildItem -LiteralPath $app -Recurse -Force -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
            Assert-True ($aside.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $app 'at-restart'))) "upgrade-aside-held: after the restart's work nothing of the setup's own is left ($($aside -join ', '))"
            Assert-True ($notDone.Count -eq 0 -and @(Get-RestartWork).Count -eq 0) "upgrade-aside-held: the restart's work in the install folder all went through, and Windows has none left ($($notDone -join ', '))"
            Write-Host 'CHECK-RAN: upgrade-aside-held'
            """.Replace("\r\n", "\n"));
        // Each check of these steps prints its CHECK-RAN marker where it must be, so CI shows that each ran.
        var unmarked = ChecksWithoutAMarker(lf, steps);
        Assert.True(unmarked.Count == 0, "checks of the steps held whole whose CHECK-RAN marker is not where it must be: " + string.Join(Environment.NewLine, unmarked));
    }

    /// <summary>
    /// The script's Format with %d only: each %d in <paramref name="format"/> in turn is given the next of
    /// <paramref name="args"/>.
    /// </summary>
    private static string PascalFormat(string format, params int[] args)
    {
        int next = 0;
        return Regex.Replace(format, "%d", _ => args[next++].ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The boxes after a /DELETEDATA uninstall started by hand, as DeleteDataWanted and ShowRestPutBackInPart in the script
    /// show them from the summary: the notice (PutBackDone, then, when it counts some of the changes put back only in part,
    /// that the others are named next; then that the data folder is to be deleted too), then the changes put back only in
    /// part past the first MaxShown, MaxShown to a box, each under a heading that says which of how many it names.
    /// </summary>
    private static List<string> DeleteDataNoticesAsTheScript(string summary, string dataDir)
    {
        int maxShown = Number("MaxShown"), maxLine = Number("MaxLineShown");
        string prefix = Constant("PartlyPutBackPrefix");
        var partly = summary.Split(Environment.NewLine).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var (partCount, _) = SummaryLinesAsTheScript(summary, prefix, maxLine);
        string notice = PutBackDoneAsTheScript(summary);
        if (SummaryNamesMoreAsTheScript(summary) && partCount > maxShown + 1)
            notice += "\r\n\r\n" + PascalFormat("The other %d changes put back only in part are named next, with what was not done.", partCount - maxShown);
        else if (SummaryNamesMoreAsTheScript(summary) && partCount == maxShown + 1)
            notice += "\r\n\r\nThe other change put back only in part is named next, with what was not done.";
        var boxes = new List<string> { notice + $"\r\n\r\nPoolWatch's reports, logs and settings ({dataDir}) are to be deleted too." };
        for (int first = maxShown; first < partly.Count; first += maxShown)
        {
            var these = partly.Skip(first).Take(maxShown).ToList();
            boxes.Add((these.Count == 1 ? PascalFormat(Constant("PartlyPutBackOneMoreHeading"), first + 1, partly.Count)
                          : PascalFormat(Constant("PartlyPutBackMoreHeading"), first + 1, first + these.Count, partly.Count)) + "\r\n" +
                      string.Concat(these.Select(l => "\r\n- " + Shorten(l[prefix.Length..], maxLine))));
        }
        return boxes;
    }

    /// <summary>
    /// A /DELETEDATA uninstall started by hand deletes uninstall-summary.txt with the data folder, so it names every change
    /// put back only in part itself: the first ten in its notice, which then says the others are named next, and the rest
    /// ten to a box, in the summary's order, each with what was not done, so each box fits on the screen. With ten or
    /// fewer, or none, the notice alone; the changes put back plainly are named as the notice names them (the first ten,
    /// the rest counted). The test holds ShowRestPutBackInPart's whole text, and the heading of its boxes.
    /// </summary>
    [Fact]
    public void TheDeleteDataNoticesNameEveryChangePutBackOnlyInPart()
    {
        Assert.Equal(OneLine("""
            procedure ShowRestPutBackInPart(const DataDir: String);
            var
              Lines: TArrayOfString;
              Shown: String;
              I, Count, Total, First: Integer;
            begin
              if not LoadStringsFromFile(DataDir + '\uninstall-summary.txt', Lines) then
                Exit;
              Total := 0;
              for I := 0 to GetArrayLength(Lines) - 1 do
                if Copy(Lines[I], 1, Length(PartlyPutBackPrefix)) = PartlyPutBackPrefix then
                  Total := Total + 1;
              Count := 0;
              First := 0;
              Shown := '';
              for I := 0 to GetArrayLength(Lines) - 1 do
                if Copy(Lines[I], 1, Length(PartlyPutBackPrefix)) = PartlyPutBackPrefix then
                begin
                  Count := Count + 1;
                  if Count > MaxShown then
                  begin
                    if Shown = '' then
                      First := Count;
                    Shown := Shown + #13#10 + '- ' + Shorten(Copy(Lines[I], Length(PartlyPutBackPrefix) + 1, Length(Lines[I])), MaxLineShown);
                    if (Count - First + 1 = MaxShown) or (Count = Total) then
                    begin
                      if First = Count then
                        MsgBox(Format(PartlyPutBackOneMoreHeading, [Count, Total]) + #13#10 + Shown, mbInformation, MB_OK)
                      else
                        MsgBox(Format(PartlyPutBackMoreHeading, [First, Count, Total]) + #13#10 + Shown, mbInformation, MB_OK);
                      Shown := '';
                    end;
                  end;
                end;
            end;
            """), Whole("ShowRestPutBackInPart"));
        Assert.Equal("PoolWatch put back these changes only in part (%d to %d of %d):", Constant("PartlyPutBackMoreHeading"));
        Assert.Equal("PoolWatch put back this change only in part (%d of %d):", Constant("PartlyPutBackOneMoreHeading"));

        const string DataDir = @"C:\ProgramData\PoolWatch";
        const string NotKnown = "the start type is back to Automatic, but it was not started again: PoolWatch does not know whether it was running when " +
                                "it was turned off. Start it from Services (services.msc) if you need it running";
        var at = new DateTime(2026, 11, 2, 9, 0, 0);
        string Summary(int partly, int plain)
        {
            var part = Enumerable.Range(1, partly).Select(i => Row($"service:s{i}", ChangeKind.Service, $"Turned off Example Service {i} (Svc{i})")).ToList();
            var done = Enumerable.Range(1, plain).Select(i => Row($"startup:{i}", ChangeKind.StartupEntry, $"Turned off the startup app Example{i}")).ToList();
            return Program.UninstallSummary([.. part, .. done], null,
                [.. part.Select(c => (c, new UndoResult(true, "...") { Caveat = NotKnown })), .. done.Select(c => (c, new UndoResult(true, "It is back.")))], at);
        }
        static string Line(int i) => $"\r\n- Turned off Example Service {i} (Svc{i}). The start type is back to Automatic, but it was not started again: " +
                                     "PoolWatch does not know whether it was running when it was turned off. Start it from Services (services.msc) if you need it running.";

        // 23 put back only in part, 12 put back plainly: the notice names the first ten of each and says the other 13 are
        // named next; then 11 to 20, then 21 to 23.
        var boxes = DeleteDataNoticesAsTheScript(Summary(23, 12), DataDir);
        Assert.Equal(3, boxes.Count);
        Assert.StartsWith("PoolWatch put back these changes only in part:\r\n" + string.Concat(Enumerable.Range(1, 10).Select(Line)) + "\r\nAnd 13 more.", boxes[0]);
        Assert.EndsWith("\r\nAnd 2 more.\r\n\r\nThe other 13 changes put back only in part are named next, with what was not done." +
                        "\r\n\r\nPoolWatch's reports, logs and settings (C:\\ProgramData\\PoolWatch) are to be deleted too.", boxes[0]);
        Assert.Equal("PoolWatch put back these changes only in part (11 to 20 of 23):\r\n" + string.Concat(Enumerable.Range(11, 10).Select(Line)), boxes[1]);
        Assert.Equal("PoolWatch put back these changes only in part (21 to 23 of 23):\r\n" + string.Concat(Enumerable.Range(21, 3).Select(Line)), boxes[2]);
        // Every one of them is named once, across the boxes.
        Assert.All(Enumerable.Range(1, 23), i => Assert.Single(boxes, b => b.Contains($"(Svc{i}).", StringComparison.Ordinal)));

        // Eleven: the notice says the other one is named next, and it is, in a box of its own, each in the singular (they
        // said "the other 1 changes" and "these changes ... (11 to 11 of 11)"). Twenty-one: the last box names the 21st alone.
        // Twenty: one more box, 11 to 20.
        var eleven = DeleteDataNoticesAsTheScript(Summary(11, 0), DataDir);
        Assert.Contains("\r\n\r\nThe other change put back only in part is named next, with what was not done.\r\n\r\n", eleven[0]);
        Assert.DoesNotContain("The other 1 ", eleven[0]);
        Assert.Equal(["PoolWatch put back this change only in part (11 of 11):\r\n" + Line(11)], eleven.Skip(1));
        Assert.Equal("PoolWatch put back this change only in part (21 of 21):\r\n" + Line(21), DeleteDataNoticesAsTheScript(Summary(21, 0), DataDir)[^1]);
        Assert.Equal(["PoolWatch put back these changes only in part (11 to 20 of 20):\r\n" + string.Concat(Enumerable.Range(11, 10).Select(Line))],
            DeleteDataNoticesAsTheScript(Summary(20, 0), DataDir).Skip(1));
        // Ten or fewer, or none put back only in part (eleven put back plainly, say): the notice alone, and it says nothing
        // is named next.
        foreach (var (partly, plain) in new[] { (10, 0), (10, 11), (3, 2), (0, 11), (0, 0) })
        {
            var only = Assert.Single(DeleteDataNoticesAsTheScript(Summary(partly, plain), DataDir));
            Assert.DoesNotContain("named next", only);
        }
    }

    /// <summary>Whether the notice names only some of a list, as SummaryNamesMore in the script decides it from the summary.</summary>
    private static bool SummaryNamesMoreAsTheScript(string summary)
    {
        var (count, _) = SummaryLinesAsTheScript(summary, Constant("PutBackPrefix"), Number("MaxSummaryShown"));
        var (partCount, _) = SummaryLinesAsTheScript(summary, Constant("PartlyPutBackPrefix"), Number("MaxLineShown"));
        return count > Number("MaxShown") || partCount > Number("MaxShown");
    }

    /// <summary>
    /// The question whether to delete the data folder, as DeleteDataWanted in the script asks it (no /DELETEDATA, not
    /// silent): "with what was not done" only when a change was put back only in part.
    /// </summary>
    private static string DeleteQuestionAsTheScript(string summary, string dataDir)
    {
        var (partCount, _) = SummaryLinesAsTheScript(summary, Constant("PartlyPutBackPrefix"), Number("MaxLineShown"));
        return PutBackDoneAsTheScript(summary) +
               (SummaryNamesMoreAsTheScript(summary)
                   ? $"\r\n\r\nuninstall-summary.txt in {dataDir} names them all{(partCount > 0 ? ", with what was not done" : "")}. Answer No to keep it."
                   : "") +
               $"\r\n\r\nAlso delete PoolWatch's reports, logs and settings ({dataDir})?";
    }

    /// <summary>
    /// The question whether to delete the data folder names the first ten changes of each list and counts the rest. When it
    /// counts some (eleven put back only in part, say: each a service not put back to Disabled and not running then, whose
    /// record does not know whether it ran, as a Turn off record of v1.0.0-beta.1 that says "not running" does), it says
    /// that uninstall-summary.txt, in the folder it asks about, names them all (with what was not done, when any was put
    /// back only in part), and that No keeps it. With ten of each it names them all itself, and says nothing of the file;
    /// nor when the summary cannot be read (a count of -1).
    /// </summary>
    [Fact]
    public void TheDeleteQuestionSaysWhereEveryChangeIsNamedWhenItNamesOnlySome()
    {
        Assert.Equal(OneLine("""
            function SummaryNamesMore(const DataDir: String; var PartCount: Integer): Boolean;
            var
              Count: Integer;
              Lines: String;
            begin
              Lines := SummaryLines(DataDir, PutBackPrefix, MaxSummaryShown, Count);
              Lines := SummaryLines(DataDir, PartlyPutBackPrefix, MaxLineShown, PartCount);
              Result := (Count > MaxShown) or (PartCount > MaxShown);
            end;
            """), Whole("SummaryNamesMore"));
        Assert.Contains("else begin Text := PutBackDone(DataDir); if SummaryNamesMore(DataDir, PartCount) then begin " +
                        "Text := Text + #13#10#13#10 + 'uninstall-summary.txt in ' + DataDir + ' names them all'; " +
                        "if PartCount > 0 then Text := Text + ', with what was not done'; Text := Text + '. Answer No to keep it.'; end; " +
                        "Result := MsgBox(Text + #13#10#13#10 + 'Also delete PoolWatch''s reports, logs and settings (' + DataDir + ')?', " +
                        "mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES; end; end;", Body("DeleteDataWanted"));
        // The file the question points to is the one the cleanup writes, in the folder it asks about.
        Assert.Equal("uninstall-summary.txt", typeof(Program).GetField("UninstallSummaryName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue());

        const string DataDir = @"C:\ProgramData\PoolWatch";
        const string NotKnown = "the start type is back to Automatic, but it was not started again: PoolWatch does not know whether it was running when " +
                                "it was turned off. Start it from Services (services.msc) if you need it running";
        var at = new DateTime(2026, 11, 1, 9, 0, 0);
        string Summary(int partly, int plain)
        {
            var part = Enumerable.Range(1, partly).Select(i => Row($"service:s{i}", ChangeKind.Service, $"Turned off Example Service {i} (Svc{i})")).ToList();
            var done = Enumerable.Range(1, plain).Select(i => Row($"startup:{i}", ChangeKind.StartupEntry, $"Turned off the startup app Example{i}")).ToList();
            return Program.UninstallSummary([.. part, .. done], null,
                [.. part.Select(c => (c, new UndoResult(true, "...") { Caveat = NotKnown })), .. done.Select(c => (c, new UndoResult(true, "It is back.")))], at);
        }

        string eleven = DeleteQuestionAsTheScript(Summary(11, 0), DataDir);
        Assert.EndsWith("\r\nAnd 1 more.\r\n\r\nuninstall-summary.txt in C:\\ProgramData\\PoolWatch names them all, with what was not done. Answer No to keep it." +
                        "\r\n\r\nAlso delete PoolWatch's reports, logs and settings (C:\\ProgramData\\PoolWatch)?", eleven);
        // Eleven put back plainly and none in part: the file names them all, and nothing was left undone, so it says
        // nothing of what was not done.
        Assert.EndsWith("\r\nAnd 1 more.\r\n\r\nuninstall-summary.txt in C:\\ProgramData\\PoolWatch names them all. Answer No to keep it." +
                        "\r\n\r\nAlso delete PoolWatch's reports, logs and settings (C:\\ProgramData\\PoolWatch)?", DeleteQuestionAsTheScript(Summary(0, 11), DataDir));
        // Eleven put back plainly and one in part (named in the box): the file says what was not done for that one too.
        Assert.Contains("names them all, with what was not done. Answer No to keep it.", DeleteQuestionAsTheScript(Summary(1, 11), DataDir));
        Assert.True(SummaryNamesMoreAsTheScript(Summary(11, 10)));
        foreach (var (partly, plain) in new[] { (10, 10), (10, 0), (0, 10), (0, 0) })
        {
            Assert.False(SummaryNamesMoreAsTheScript(Summary(partly, plain)), $"{partly} partly, {plain} plainly");
            Assert.DoesNotContain("uninstall-summary.txt", DeleteQuestionAsTheScript(Summary(partly, plain), DataDir));
        }
        // A summary that cannot be read counts -1, never more than MaxShown.
        Assert.Contains("Count := -1; if not LoadStringsFromFile(", Body("SummaryLines"));
    }

    /// <summary>
    /// CI's installer test turns a service off with this build, then uninstalls: that service is expected back whole, as a
    /// "Put back:" line of the summary, and the test takes no other line for it (a "Partly put back:" one fails it).
    /// </summary>
    [Fact]
    public void TheInstallerTestExpectsItsServicePutBackWhole()
    {
        string? repo = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && repo is null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) repo = dir.FullName;
        string e2e = File.ReadAllText(Path.Combine(repo!, "ci", "installer-e2e.ps1"));
        string check = Assert.Single(e2e.Split('\n'), l => l.Contains("\"uninstall-summary.txt lists $service as put back\"", StringComparison.Ordinal));
        Assert.Contains("($summary -match \"(?m)^" + Constant("PutBackPrefix") + @".*\b$([regex]::Escape($service))\b"")", check);
        Assert.DoesNotContain("Partly", check);
        // The check is a statement of the script's top level with its CHECK-RAN marker next, so CI shows that it ran.
        Assert.True(ScriptCode.CheckThenMarker(e2e, "Assert-True ($summary -match \"(?m)^" + Constant("PutBackPrefix"), "installer-summary-lists-service"), "the summary's check is not a statement of the script's top level with its marker next");
    }

    /// <summary>
    /// The script's [Code] keeps its comments and its code wrapped at 120 columns, as the script is written: no line is
    /// longer, unless its code holds a string (a line break lost in an edit left one of 144 in a comment). A line whose
    /// code holds a string may run longer, so that a message is never split for width alone; an apostrophe in a comment
    /// is no string, so a comment line is always measured.
    /// </summary>
    [Fact]
    public void TheScriptsCodeAndCommentsStayWrapped()
    {
        var lines = CodeSection().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var strings = LinesWithAString(lines);
        var wide = lines.Select((l, i) => (Line: l, At: i + 1)).Where(x => !strings[x.At - 1] && x.Line.Length > 120)
            .Select(x => $"[Code] line {x.At}: {x.Line.Length} columns").ToList();
        Assert.True(wide.Count == 0, string.Join("; ", wide));
        // The measure reads the lines it means: most lines, comments with an apostrophe among them, and not a string.
        Assert.True(strings.Count(s => !s) > lines.Count / 2, "the measure reads too few lines");
        Assert.Equal([false, true, false, false, true, false],
            LinesWithAString(["  { The script's own words,", "    which go on } Log('a message');", "    x := 1; // isn't one", "  { one more }",
                "  Result := 'a ' + ", "  {}"]));
    }

    /// <summary>
    /// This file's comments keep their paragraphs wrapped: no comment line (<c>//</c> or <c>///</c>) of under 100 columns
    /// stops in the middle of a sentence while the next line goes on with it and that line's first word would fit on it
    /// within 120 columns (an edit that left a line short, as one in the boxes' test did). A line that ends a sentence or
    /// a clause (with . : ; ? or !), one before a line that starts with a bracket or a tag, and one before the next
    /// paragraph is not held, so the file's own breaks before a reference stay as they are.
    /// </summary>
    [Fact]
    public void ThisFilesCommentsStayWrapped()
    {
        static List<string> Short(IReadOnlyList<string> lines)
        {
            var found = new List<string>();
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                var a = Regex.Match(lines[i], @"^(\s*///?) (.*\S)$");
                var b = Regex.Match(lines[i + 1], @"^(\s*///?) ([^\s(<\-]\S*)");
                if (!a.Success || !b.Success || a.Groups[1].Value != b.Groups[1].Value) continue;
                if (lines[i].Length >= 100 || Regex.IsMatch(a.Groups[2].Value, @"[.:;?!]$|<(/?summary|/?para|/?list|item|/item|/?remarks)\b")) continue;
                if (lines[i].Length + 1 + b.Groups[2].Value.Length <= 120) found.Add($"line {i + 1}: {lines[i].Trim()}");
            }
            return found;
        }
        var lines = Source("tests", "PoolWatch.AppTests", "UninstallerTests.cs").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var left = Short(lines);
        Assert.True(left.Count == 0, string.Join(Environment.NewLine, left));
        // The rule finds the line an edit left short, and passes the breaks it leaves alone.
        Assert.Single(Short(["        // more, and, when there is a log, that the log names each. The log also names each",
            "        // process with PoolWatch's name the wait saw"]));
        Assert.Empty(Short(["    /// one ends a sentence.", "    /// The next one starts.", "    /// a break before", "    /// (<see cref=\"X\"/>) a reference",
            "        // a comment", "    /// of another kind"]));
    }

    /// <summary>
    /// For each line of Pascal code, whether its code (outside its { } and // comments) holds a string: a quote outside a
    /// comment. A { } comment goes on over the lines up to its closing brace; a brace in a string is no comment.
    /// </summary>
    private static List<bool> LinesWithAString(IReadOnlyList<string> lines)
    {
        var found = new List<bool>();
        bool comment = false;
        foreach (string line in lines)
        {
            bool inString = false, has = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (comment) { if (c == '}') comment = false; continue; }
                if (inString) { if (c == '\'') inString = false; continue; }
                if (c == '{') comment = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                else if (c == '\'') { inString = true; has = true; }
            }
            found.Add(has);
        }
        return found;
    }

    /// <summary>
    /// SECURITY.md's row for the setup says what the setup does with 1.0.0-beta.1's uninstaller and what holds only while
    /// Windows takes the restart work: the record, or else the messages file, or else the program set aside, and the
    /// uninstaller free to run only when none of the three can be; this version's uninstaller removing nothing unless
    /// Windows refused some of that work; a new file Windows would not take keeping the launcher's files (and, at a
    /// launcher's name, every old file) set aside; and no file that starts PoolWatch left at the launcher's names unless
    /// Windows refuses that too. And the row and the setup's comments on what it may do before it changes PoolWatch's
    /// files say that it closes no program (<see cref="TheSetupClosesNoProgram"/>).
    /// </summary>
    [Fact]
    public void TheSecurityRowForTheSetupSaysWhatHoldsWhenWindowsRefusesTheRestartWork()
    {
        string security = OneLine(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "SECURITY.md")));
        Assert.Contains("Until that restart this version's uninstaller removes nothing, unless Windows refused some of that work (the setup then says to run " +
                        "it again before uninstalling), nor does 1.0.0-beta.1's while the setup has its record (`unins000.dat`), or else its messages file " +
                        "(`unins000.msg`), or else its program (`unins000.exe`; the setup then says to answer No if Windows offers to remove PoolWatch from " +
                        "its list of apps), set aside until then; when the setup can set none of them aside (another program keeps all three from being " +
                        "renamed, or Windows will not take them for the restart), that uninstaller can run before the restart", security);
        Assert.Contains("it stays after it; when it stays under its own name, the launcher's files stay set aside too, and every old file does when it " +
                        "is at one of the launcher's names: see Known limitations)", security);
        Assert.Contains("it then leaves no file that starts PoolWatch at the launcher's names after the restart, unless Windows refuses that too,", security);
        Assert.DoesNotContain("when the setup can set neither aside", security);
        // What it says of the held program is what the setup's box says.
        Assert.Contains("'Windows offers to remove PoolWatch from its list of apps before then, answer No.'", CodeSection());
        string code = OneLine(CodeSection());
        Assert.Contains("and deleted what an earlier setup left in its own folder for a restart that has happened since, WhenToReplace: nothing a step " +
                        "puts back; it closes no program, CloseApplications being off)", code);
        Assert.Contains("and deleted what an earlier setup left in its own folder for a restart that has happened since (WhenToReplace), none of which " +
                        "a step puts back (it closes no program: CloseApplications is off)", code);
        Assert.Contains("Inno Setup closes no program after this (CloseApplications is off): a file another program keeps in use sends the update to " +
                        "the next restart (WhenToReplace, SetAllAside).", code);
        Assert.DoesNotContain("CloseApplications fallback", code);
        Assert.Contains("It closes no other program, another account's or a service included: Inno Setup's own close of the programs that have a file " +
                        "of the setup open is off, so a file another program keeps in use sends the update to the next restart.", security);
        Assert.Contains("CloseApplications=no", Script());
    }
}
