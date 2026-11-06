using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace PoolWatch.AppTests;

/// <summary>
/// How the text pins read a script (<see cref="ScriptCode"/>): on the repository's own scripts, and on copies of them
/// changed as an edit would hide a change from a pin that reads the text as written: a full copy of a pinned definition in
/// a comment before the real one, with the real one edited; a second definition; a pinned check commented out, in a
/// string, or moved into another block; a pinned Pascal statement under a changed condition, or in a block it was not in.
/// Each such copy fails its pin. Whether the code runs is not what a pin reads: for the CI scripts' checks, the markers
/// show that in CI (<see cref="CheckMarkerTests"/>).
/// </summary>
public class ScriptCodeTests
{
    private const string Stop = "Assert-StoppedBySignal $pw 'uninstaller'", Job = "Assert-HelperInJob $pw 'tray-1'";

    private const string RanId = "installer-checked-after-uninstall";

    private static readonly string Ran = ScriptCode.Marker(RanId);

    private static string Repo(params string[] path) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path]));

    private static string OneLine(string text) => UninstallerTests.OneLine(text);

    /// <summary><paramref name="text"/> with <paramref name="find"/>, which must be there once, replaced.</summary>
    private static string Once(string text, string find, string replace)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(find, at + 1, StringComparison.Ordinal) < 0, $"\"{find}\" is not in the text once");
        return text[..at] + replace + text[(at + find.Length)..];
    }

    /// <summary>
    /// Why a pin fails on a script: the message of its read when the read fails, or a fixed text when the read gives code
    /// in which <paramref name="holds"/> does not find what the pin holds; null when the pin holds.
    /// </summary>
    internal static string? WhyThePinFails(Func<bool> holds)
    {
        try
        {
            return holds() ? null : NotThere;
        }
        catch (XunitException e)
        {
            return e.Message;
        }
    }

    /// <summary>What <see cref="WhyThePinFails"/> gives when the read is fine but what the pin holds is not in it.</summary>
    internal const string NotThere = "what the pin holds is not in the code";

    private static string E2e() => Repo("ci", "installer-e2e.ps1");

    /// <summary>
    /// Whether the installer test's pins (<c>TokenLeakHelperTests.TheInstallerTestLooksForTheLinesTheGuardLogs</c>) hold on
    /// <paramref name="e2e"/>: Assert-HelperInJob is the real script's, and the uninstall's stop check and its job check
    /// are a run of checks of the script's top level with their marker right after it, the job check there once
    /// (<see cref="ScriptCode.CheckThenMarker(string, IReadOnlyList{string}, string, string[])"/>).
    /// </summary>
    private static bool TheInstallerPinsHold(string e2e)
    {
        string real = OneLine(ScriptCode.PowerShellFunction(E2e(), "Assert-HelperInJob").Value);
        return OneLine(ScriptCode.PowerShellFunction(e2e, "Assert-HelperInJob").Value) == real && ScriptCode.CheckThenMarker(e2e, [Stop, Job], RanId);
    }

    /// <summary><paramref name="definition"/> with <paramref name="inserted"/> as the first line after its first <paramref name="opening"/>.</summary>
    private static string Early(string definition, string opening, string inserted)
    {
        var first = new Regex($@"(?m){Regex.Escape(opening)}[ \t]*\r?$");
        Assert.Matches(first, definition);
        return first.Replace(definition, m => m.Value.TrimEnd('\r') + Environment.NewLine + inserted, 1);
    }

    // ---------------------------------------------------------------- a copy of a pinned definition in a comment

    /// <summary>
    /// A full copy of a routine (here HasSwitch; the same for the uninstaller's NoneRunsFromApp and RunningFromApp) in a
    /// comment just before the real one, which is made to exit at once. A reader that takes the first line that starts
    /// the routine reads the copy; the pin reads the real one, whichever kind of comment holds the copy, and a second real
    /// one, or none, fails it.
    /// </summary>
    [Theory]
    [InlineData("{", "}")]
    [InlineData("(*", "*)")]
    [InlineData("//", "")]
    public void APascalRoutineIsTheRealOneNotACopyInAComment(string open, string close)
    {
        string code = UninstallerTests.CodeSection();
        string real = ScriptCode.PascalRoutine(code, "HasSwitch");
        Assert.DoesNotContain("}", real); // so a { } comment can hold all of it
        string copy = open == "//" ? Regex.Replace(real, "(?m)^", "//") : open + Environment.NewLine + real + Environment.NewLine + close;
        string editedReal = Early(real, "begin", "  Result := True; Exit;");
        string changed = Once(code, real, copy + Environment.NewLine + Environment.NewLine + editedReal);
        if (open != "//") Assert.Equal(OneLine(real), OneLine(Regex.Match(changed, @"(?ms)^function HasSwitch\b.*?^end;").Value)); // the first match is the copy
        string found = ScriptCode.PascalRoutine(changed, "HasSwitch");
        Assert.Contains("Result := True; Exit;", found);
        Assert.NotEqual(OneLine(real), OneLine(found));
        Assert.Contains("Result := True; Exit;", ScriptCode.PascalRoutine(ScriptCode.Pascal(changed), "HasSwitch")); // read without comments, the same
        // A second real definition (one a later edit added beside the first) is not taken for either.
        Assert.ThrowsAny<XunitException>(() => ScriptCode.PascalRoutine(code + Environment.NewLine + real, "HasSwitch"));
        // A routine that is only named, never defined, fails too.
        Assert.ThrowsAny<XunitException>(() => ScriptCode.PascalRoutine(code, "NoSuchRoutine"));
    }

    /// <summary>
    /// The installer test's check: a full copy of Assert-HelperInJob in <c>&lt;# #&gt;</c> before the real one, which is
    /// made to return at once. The pin reads the real one; a second definition in the script's code fails it, wherever it
    /// is: with a scope, in another case, as a filter, inside a block, or in a branch that never runs.
    /// </summary>
    [Fact]
    public void APowerShellFunctionIsTheRealOneNotACopyInAComment()
    {
        string e2e = E2e();
        string real = ScriptCode.PowerShellFunction(e2e, "Assert-HelperInJob").Value;
        string edited = Once(e2e, real, Early(real, "{", "    return"));
        string changed = Once(edited, "function Assert-HelperInJob(", "<#" + Environment.NewLine + real + Environment.NewLine + "#>" + Environment.NewLine + "function Assert-HelperInJob(");
        Assert.Equal(OneLine(real), OneLine(Regex.Match(changed, @"(?ms)^function Assert-HelperInJob\(.*?^\}").Value)); // the first match is the copy
        string found = ScriptCode.PowerShellFunction(changed, "Assert-HelperInJob").Value;
        Assert.Matches(@"\{\r?\n    return\r?\n", found);
        Assert.NotEqual(OneLine(real), OneLine(found));
        string Beside(string added) => Once(e2e, real, real + Environment.NewLine + added);
        foreach (string added in new[]
        {
            real, "function script:Assert-HelperInJob($running, [string]$name) { }", "function global:ASSERT-HELPERINJOB { }", "filter Assert-HelperInJob { }",
            "if ($env:CI) {" + Environment.NewLine + real + Environment.NewLine + "}", "if ($false) {" + Environment.NewLine + real + Environment.NewLine + "}",
        })
            Assert.Contains("defines Assert-HelperInJob 2 times", Assert.ThrowsAny<XunitException>(() => ScriptCode.PowerShellFunction(Beside(added), "Assert-HelperInJob")).Message);
        // One definition, but not a statement of the script's top level: inside a block.
        string inBlock = Once(e2e, real, "if ($env:CI) {" + Environment.NewLine + real + Environment.NewLine + "}");
        Assert.Contains("not defined by a statement of the script's top level", Assert.ThrowsAny<XunitException>(() => ScriptCode.PowerShellFunction(inBlock, "Assert-HelperInJob")).Message);
    }

    /// <summary>
    /// The guard's exit failsafe: a full copy of HookProcessExit in a block comment before the real one, which is made to
    /// return at once; and the line that passes it as the hook, moved into a comment or a string. The pin reads the real
    /// member and the code.
    /// </summary>
    [Fact]
    public void ACSharpMemberIsTheRealOneNotACopyInACommentOrAString()
    {
        string source = Repo("src", "PoolWatch", "Engine", "TokenLeakGuard.cs");
        const string header = "private static void HookProcessExit(";
        string real = ScriptCode.CSharpMember(source, header).Value;
        string edited = Once(source, real, Early(real, "{", "        return;"));
        string changed = Once(edited, "    " + header, "/*" + Environment.NewLine + "    " + real + Environment.NewLine + "*/" + Environment.NewLine + "    " + header);
        Assert.Equal(OneLine(real), OneLine(Regex.Match(changed, @"(?ms)^    private static void HookProcessExit\(.*?^    \}").Value)); // the first match is the copy
        string found = ScriptCode.CSharpMember(changed, header).Value;
        Assert.Contains("return;", found);
        Assert.NotEqual(OneLine(real), OneLine(found));
        Assert.ThrowsAny<XunitException>(() => ScriptCode.CSharpMember(Once(source, "    " + header, "    " + real + Environment.NewLine + "    " + header), header));
        // The hook passed in the code, not in a comment beside a different one.
        const string hookLine = "hookExit: HookProcessExit, setTo:", end = "signIn: TokenGuardNative.SignInId) { }";
        string hook = Once(source, hookLine, "hookExit: a => HookProcessExit(() => { }), /* hookExit: HookProcessExit, setTo: */ setTo:");
        Assert.Contains(hookLine, OneLine(hook));
        Assert.DoesNotContain(hookLine, OneLine(ScriptCode.CSharp(hook)));
        // Nor in a string beside a call that passes no hook: a regular, a verbatim or a raw string constant.
        foreach (string literal in new[] { "\"hookExit: HookProcessExit, setTo:\"", "@\"hookExit: HookProcessExit, setTo:\"", "\"\"\"hookExit: HookProcessExit, setTo:\"\"\"" })
        {
            string inString = Once(Once(source, hookLine, "hookExit: null, setTo:"), end, $"{end}{Environment.NewLine}    private const string OldHook = {literal};");
            Assert.Contains(hookLine, OneLine(inString));
            Assert.DoesNotContain(hookLine, OneLine(ScriptCode.CSharp(inString)));
            Assert.Contains("hookExit: null, setTo:", OneLine(ScriptCode.CSharp(inString)));
            Assert.Contains(end + " private const string OldHook = \"\";", OneLine(ScriptCode.CSharp(inString)));
        }
    }

    /// <summary>
    /// The C# pins do not read the code under an <c>#if</c> as code, as a build may leave it out: the guard's public
    /// constructor, which passes the exit failsafe as its hook, and HookProcessExit itself, put under <c>#if DEBUG</c> with
    /// an <c>#else</c> constructor that passes a hook doing nothing (a Release build with no failsafe), are found by neither
    /// the line pin nor the member pin; and a member with an <c>#if</c> inside it fails the member pin.
    /// </summary>
    [Fact]
    public void ACSharpPinDoesNotReadTheCodeUnderAnIf()
    {
        string source = Repo("src", "PoolWatch", "Engine", "TokenLeakGuard.cs");
        const string header = "private static void HookProcessExit(", hook = "hookExit: HookProcessExit, setTo:", end = "signIn: TokenGuardNative.SignInId) { }";
        string member = ScriptCode.CSharpMember(source, header).Value;
        Assert.Contains(hook, OneLine(ScriptCode.CSharp(source)));
        int from = source.IndexOf("    public TokenLeakGuard(AppPaths paths, Log log, Action<Finding> finding)", StringComparison.Ordinal);
        Assert.True(from >= 0, "the guard's public constructor is not in its source");
        string ctor = source[from..(source.IndexOf(end, from, StringComparison.Ordinal) + end.Length)];
        Assert.Contains(hook, ctor);
        string nl = Environment.NewLine;
        string underIf = Once(Once(source, ctor, $"#if DEBUG{nl}{ctor}{nl}#else{nl}{ctor.Replace(hook, "hookExit: _ => { }, setTo:", StringComparison.Ordinal)}{nl}#endif"),
            member, $"#if DEBUG{nl}    {member}{nl}#endif");
        Assert.Contains(hook, OneLine(underIf)); // still there as text
        Assert.DoesNotContain(hook, OneLine(ScriptCode.CSharp(underIf)));
        Assert.DoesNotContain("hookExit: _ => { }, setTo:", OneLine(ScriptCode.CSharp(underIf))); // nor the other branch
        Assert.Contains("the source has 0 members that start", Assert.ThrowsAny<XunitException>(() => ScriptCode.CSharpMember(underIf, header)).Message);
        const string exit = "AppDomain.CurrentDomain.ProcessExit += (_, _) => onExit();";
        string inside = Once(source, exit, $"{nl}#if DEBUG{nl}        {exit}{nl}#endif");
        Assert.Contains("has an #if in it", Assert.ThrowsAny<XunitException>(() => ScriptCode.CSharpMember(inside, header)).Message);
    }

    /// <summary>
    /// The names of Pascal code's routines are read from the headers in its code: a header in a comment or a string is not
    /// one, nor <c>procedure</c> or <c>function</c> after an equals sign or a colon (a procedural type's, a variable's) or
    /// with no word after it (<c>array of procedure;</c>); a routine Windows provides is one, and so is a header in
    /// capitals. A routine is read from its header to its own last token, as written, its comments too (one Windows
    /// provides to the semicolon after its <c>external '...'</c>), and what is outside the routines is every other token
    /// of the code, comments left out.
    /// </summary>
    [Fact]
    public void TheNamesOfTheRoutinesAreReadFromTheHeadersInTheCode()
    {
        string code = string.Join(Environment.NewLine,
            "type",
            "  TNotify = procedure of object;",
            "  THooks = array of procedure;",
            "var",
            "  Hook: function of object;",
            "{ function InBraces: Boolean; }",
            "(* procedure InParens; *)",
            "// procedure InLine;",
            "function Real(const S: String): Boolean;",
            "begin",
            "  Result := S = 'function InString';",
            "end;",
            "function Provided(Opened: THandle): BOOL; { closes it }",
            "  external 'CloseHandle@kernel32.dll stdcall';",
            "PROCEDURE Other;",
            "begin",
            "end;",
            "const",
            "  Last = 1;");
        Assert.Equal(["Real", "Provided", "Other"], ScriptCode.PascalRoutineNames(code));
        Assert.Equal("function Provided(Opened: THandle): BOOL; { closes it } external 'CloseHandle@kernel32.dll stdcall';",
            OneLine(ScriptCode.PascalRoutine(code, "Provided")));
        Assert.Equal("PROCEDURE Other; begin end;", OneLine(ScriptCode.PascalRoutine(code, "Other")));
        Assert.Equal(
            [
                "type", "TNotify", "=", "procedure", "of", "object", ";", "THooks", "=", "array", "of", "procedure", ";",
                "var", "Hook", ":", "function", "of", "object", ";", "const", "Last", "=", "1", ";",
            ],
            ScriptCode.PascalOutsideRoutines(code));
        // An external declaration that is not external '...'; is not read as one.
        Assert.ThrowsAny<XunitException>(() => ScriptCode.PascalRoutine("function F: BOOL;" + Environment.NewLine + "  external Name;", "F"));
    }

    /// <summary>
    /// A comment in parentheses ends at the first <c>*)</c> that starts at its <c>*</c> or later, as Pascal Script ends it:
    /// so <c>(*)</c> is a whole comment, and what follows it is code, an event attribute and a routine there too.
    /// </summary>
    [Fact]
    public void AParenthesisCommentEndsWherePascalScriptEndsIt()
    {
        string nl = Environment.NewLine;
        string code = string.Join(nl,
            "var A: Boolean;",
            "(*)",
            "<event('InitializeWizard')>",
            "procedure Found;",
            "begin",
            "end;",
            "//*)",
            "(**) (* a *) procedure Seen;",
            "begin",
            "end;");
        Assert.Equal(["Found", "Seen"], ScriptCode.PascalRoutineNames(code));
        Assert.Equal(["var", "A", ":", "Boolean", ";", "<", "event", "(", "'InitializeWizard'", ")", ">"], ScriptCode.PascalOutsideRoutines(code));
        Assert.StartsWith($"var A: Boolean;{nl}{{}}{nl}<event('InitializeWizard')>{nl}procedure Found", ScriptCode.Pascal(code));
    }

    /// <summary>
    /// No reserved word is taken for a constant's or a variable's name, in any case: neither one of the 65 that Pascal
    /// Script's tokenizer reserves (its LookupTable in uPSUtils.pas, Inno Setup 6.7.3) nor another of Delphi's reserved
    /// words. A name that only starts with one is a name.
    /// </summary>
    [Fact]
    public void NoReservedWordIsTakenForAName()
    {
        string[] pascalScript =
        [
            "AND", "ARRAY", "AS", "BEGIN", "CASE", "CHR", "CLASS", "CONST", "CONSTRUCTOR", "DESTRUCTOR", "DIV", "DO", "DOWNTO",
            "ELSE", "END", "EXCEPT", "EXIT", "EXPORT", "EXTERNAL", "FINALIZATION", "FINALLY", "FOR", "FORWARD", "FUNCTION",
            "GOTO", "IF", "IMPLEMENTATION", "IN", "INHERITED", "INITIALIZATION", "INTERFACE", "IS", "LABEL", "MOD", "NIL",
            "NOT", "OF", "OR", "ORD", "OUT", "OVERRIDE", "PRIVATE", "PROCEDURE", "PROGRAM", "PROPERTY", "PROTECTED", "PUBLIC",
            "PUBLISHED", "RECORD", "REPEAT", "SET", "SHL", "SHR", "THEN", "TO", "TRY", "TYPE", "UNIT", "UNTIL", "USES", "VAR",
            "VIRTUAL", "WHILE", "WITH", "XOR",
        ];
        string[] delphi = ["asm", "dispinterface", "exports", "file", "inline", "library", "object", "packed", "raise", "resourcestring", "string", "threadvar"];
        Assert.Equal(65, pascalScript.Distinct().Count());
        string? Failure(string code) => UninstallerTests.PlainDeclarationsFailure(ScriptCode.PascalOutsideRoutines(code));
        foreach (string word in pascalScript.Concat(delphi))
        {
            string mixed = char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
            Assert.True(Failure($"const {word} = 1;") is not null, $"{word} is taken for a constant's name");
            Assert.True(Failure($"var {mixed}: Integer;") is not null, $"{mixed} is taken for a variable's name");
            Assert.Null(Failure($"const {mixed}1 = 1; var {word}_X: Integer;"));
        }
    }

    /// <summary>
    /// What [Code] may have outside its routines: sections of constants and variables, each declared plainly
    /// (<see cref="UninstallerTests.PlainDeclarationsFailure"/>). An event attribute, a type, a directive, a statement, a
    /// constant set to a sum or a name, a section with no declaration, or nothing at all, is not that.
    /// </summary>
    [Fact]
    public void OutsideTheRoutinesOnlyPlainDeclarationsAreTaken()
    {
        string? Failure(string code) => UninstallerTests.PlainDeclarationsFailure(ScriptCode.PascalOutsideRoutines(code));
        Assert.Null(Failure("const A = 1; B = -2; C = $1F; D = 'it''s'; var E: Integer; F, G: String; H: array of Cardinal; J: array[0..2] of TBitmapImage; const I = 3;"));
        Assert.Null(Failure("var A: Boolean; { a comment } procedure P; begin end; function Q: Boolean; begin end; var B: Integer;"));
        foreach (string code in new[]
        {
            "var A: Boolean; <event('InitializeSetup')> function Q: Boolean; begin Result := False; end;",
            "type T = Integer; var A: T;",
            "var A: Boolean; #include \"extra.iss\"",
            "var A: Boolean; begin end.",
            "const A = 1 + 2;",
            "const A = B;",
            "const A = -'x';",
            "const begin = 1;",
            "var A: Integer = 5;",
            "var A: array[0..N] of Integer;",
            "var A: array[0..2 of Integer;",
            "const",
            "A = 1;",
            "",
            "var A: Boolean; [Files]",
        })
            Assert.NotNull(Failure(code));
    }

    /// <summary>
    /// The tokens of C# code leave out its comments, its preprocessor lines and the code of an <c>#if</c>, and keep each
    /// string whole; so the whole-routine pins of a test file are read only where they are code: a pin in a comment, in a
    /// string or under an <c>#if</c> is not one, nor a pin whose text is not a raw string.
    /// </summary>
    [Fact]
    public void TheWholeRoutinePinsAreReadOnlyInTheCode()
    {
        string nl = Environment.NewLine, raw = "\"\"\"";
        Assert.Equal(["int", "x", "=", "\"a // b\"", ";", "y", "(", ")", ";"],
            ScriptCode.CSharpTokens($"int x = \"a // b\"; // c{nl}/* d */{nl}#if DEBUG{nl}z();{nl}#else{nl}w();{nl}#endif{nl}#region e{nl}y();{nl}#endregion"));
        string Pin(string name) => $"Assert.Equal(OneLine({raw}{nl}    function {name}: Boolean;{nl}    {raw}), Whole(\"{name}\"));";
        string source = string.Join(nl,
            Pin("A"),
            Regex.Replace(Pin("B"), "(?m)^", "// "),
            "/* " + Pin("C") + " */",
            "const string D = \"Assert.Equal(OneLine(x), Whole(\\\"D\\\"));\";",
            "#if DEBUG", Pin("E"), "#endif",
            "Assert.Equal(OneLine(\"function F: Boolean;\"), Whole(\"F\"));",
            Pin("G"));
        var pins = UninstallerTests.WholePins(source);
        Assert.Equal(["A", "G"], pins.Select(p => p.Name));
        Assert.Equal(["function A: Boolean;", "function G: Boolean;"], pins.Select(p => OneLine(p.Text)));
    }

    /// <summary>
    /// How a loop's list is read: the statement's head before its block, and the list's strings when it is strings in
    /// single quotes and nothing else. A pipe or an index after the list, a variable or a sum in it, is not such a list.
    /// </summary>
    [Fact]
    public void ALoopsListIsReadAsStringsAndNothingElse()
    {
        Assert.Equal("foreach ($item in 'a', 'b''s', 'c')", ScriptCode.Head("foreach ($item in 'a',\n    'b''s', 'c') {\n  Assert-True ($item -in $x) \"has '$item'\"\n}"));
        Assert.Equal(["a", "b's", "c"], ScriptCode.StringList("'a', 'b''s',  'c'"));
        Assert.Equal(["a"], ScriptCode.StringList(" 'a' "));
        foreach (string other in new[] { "'a', 'b' | Select-Object -First 1", "@('a', 'b')[0]", "'a', $b", "'a' + 'b'", "('a', 'b')", "'a',", "", "'a' 'b'", "\"a\", 'b'" })
            Assert.Null(ScriptCode.StringList(other));
    }

    // ---------------------------------------------------------------- a pinned check commented out, quoted or moved

    /// <summary>
    /// The installer test checks each run's job right after the check that the run was stopped through its exit signal,
    /// with their marker next. Each copy here wraps the three lines together in a block comment, a here-string, or a
    /// branch or loop (one that never runs, or one that may not), or comments each out: they still read as they did to a
    /// pin of the text, one after the other, and the pin fails.
    /// </summary>
    [Theory]
    [InlineData("<#", "#>")]
    [InlineData("if ($false) {", "}")]
    [InlineData("if ($pw -eq 'never') {", "}")]
    [InlineData("foreach ($x in @()) {", "}")]
    [InlineData("$null = @'", "'@")]
    [InlineData("#", "")]
    public void ThePinnedStopAndJobChecksMustBeStatementsOfTheScript(string before, string after)
    {
        string e2e = E2e();
        Assert.True(TheInstallerPinsHold(e2e));
        var three = Regex.Match(e2e, $@"(?m)^{Regex.Escape(Stop)}\r?\n{Regex.Escape(Job)}\r?\n{Regex.Escape(Ran)}\r?$");
        Assert.True(three.Success, "the two checks and their marker are not on consecutive lines of ci/installer-e2e.ps1");
        string lines = three.Value.TrimEnd('\r');
        string changed = before == "#"
            ? e2e[..three.Index] + Regex.Replace(lines, "(?m)^", "# ") + e2e[(three.Index + three.Length)..]
            : e2e[..three.Index] + before + Environment.NewLine + lines + Environment.NewLine + after + e2e[(three.Index + three.Length)..];
        Assert.Contains(Stop, changed); // what the old pin held, still there as text
        Assert.Contains(Job, changed);
        Assert.NotNull(WhyThePinFails(() => TheInstallerPinsHold(changed)));
    }

    /// <summary>
    /// <see cref="ScriptCode.CheckThenMarker"/> holds a check of its own in its block with its marker as the next statement:
    /// not one in another block, not one whose marker is missing, has another id or comes later, and not two of them;
    /// <see cref="ScriptCode.OnlyCheckThenMarker"/> holds a block (a loop's body) that is only the check and its marker, so
    /// neither a statement before the check (a guarded continue) nor one after the marker (a guarded break that ends the
    /// loop once its first round printed the marker) is there. Given a run of checks, CheckThenMarker holds the last a
    /// check of its own in the block, the others right before it in order, and the marker right after the last: not a run
    /// with another statement in it, in another order or with its first check missing or in another block, not one whose
    /// marker comes later or has another id, and not two of them.
    /// </summary>
    [Fact]
    public void AMarkerPinHoldsTheCheckAndItsMarkerAsConsecutiveStatementsOfTheBlock()
    {
        string Script(string body) => "if ($a) {\n" + body + "\n}\n";
        const string check = "Assert-True ($x) \"", marked = "Assert-True ($x) \"x\"\nWrite-Host 'CHECK-RAN: x'";
        Assert.True(ScriptCode.CheckThenMarker(Script(marked), check, "x", "if ($a) {"));
        Assert.True(ScriptCode.OnlyCheckThenMarker(Script(marked), check, "x", "if ($a) {"));
        Assert.True(ScriptCode.CheckThenMarker(Script("$y = 1\n" + marked), check, "x", "if ($a) {"));
        Assert.False(ScriptCode.OnlyCheckThenMarker(Script("$y = 1\n" + marked), check, "x", "if ($a) {")); // not first
        Assert.False(ScriptCode.OnlyCheckThenMarker(Script("if ($x -eq 'b') { continue }\n" + marked), check, "x", "if ($a) {")); // a continue before it
        Assert.True(ScriptCode.CheckThenMarker(Script(marked + "\nif ($x -eq 'a') { break }"), check, "x", "if ($a) {"));
        Assert.False(ScriptCode.OnlyCheckThenMarker(Script(marked + "\nif ($x -eq 'a') { break }"), check, "x", "if ($a) {")); // a break after the marker
        Assert.False(ScriptCode.OnlyCheckThenMarker(Script(check + "x\"\nWrite-Host 'CHECK-RAN: y'"), check, "x", "if ($a) {")); // another id
        Assert.False(ScriptCode.CheckThenMarker(Script(marked), check, "y", "if ($a) {")); // another id
        Assert.False(ScriptCode.CheckThenMarker(Script("Assert-True ($x) \"x\""), check, "x", "if ($a) {")); // no marker
        Assert.False(ScriptCode.CheckThenMarker(Script("Assert-True ($x) \"x\"\n$y = 1\nWrite-Host 'CHECK-RAN: x'"), check, "x", "if ($a) {")); // later
        Assert.False(ScriptCode.CheckThenMarker(Script(marked + "\n" + marked), check, "x", "if ($a) {")); // twice
        Assert.False(ScriptCode.CheckThenMarker(Script("if ($b) {\n" + marked + "\n}"), check, "x", "if ($a) {")); // in another block
        Assert.False(ScriptCode.CheckThenMarker(Script("# " + marked), check, "x", "if ($a) {")); // commented out
        Assert.False(ScriptCode.CheckThenMarker(Script(marked) + Script(marked), check, "x", "if ($a) {")); // the block not there once
        Assert.True(ScriptCode.CheckThenMarker(marked, check, "x")); // at the top level
        // A run of checks, its marker right after the last.
        string[] run = ["Stop-Check $x", "Job-Check $x"];
        const string ran = "Stop-Check $x\nJob-Check $x\nWrite-Host 'CHECK-RAN: x'";
        Assert.True(ScriptCode.CheckThenMarker(Script(ran), run, "x", "if ($a) {"));
        Assert.True(ScriptCode.CheckThenMarker(ran, run, "x")); // at the top level
        Assert.True(ScriptCode.CheckThenMarker(Script("Stop-Check $x\n$y = 1\n" + ran), run, "x", "if ($a) {")); // its first check again, elsewhere
        Assert.False(ScriptCode.CheckThenMarker(Script("Stop-Check $x\n$y = 1\nJob-Check $x\nWrite-Host 'CHECK-RAN: x'"), run, "x", "if ($a) {")); // another statement in it
        Assert.False(ScriptCode.CheckThenMarker(Script("Job-Check $x\nStop-Check $x\nWrite-Host 'CHECK-RAN: x'"), run, "x", "if ($a) {")); // another order
        Assert.False(ScriptCode.CheckThenMarker(Script("Job-Check $x\nWrite-Host 'CHECK-RAN: x'"), run, "x", "if ($a) {")); // its first check missing
        Assert.False(ScriptCode.CheckThenMarker(Script("if ($b) { Stop-Check $x }\nJob-Check $x\nWrite-Host 'CHECK-RAN: x'"), run, "x", "if ($a) {")); // in another block
        Assert.False(ScriptCode.CheckThenMarker(Script("Stop-Check $x\nJob-Check $x\n$y = 1\nWrite-Host 'CHECK-RAN: x'"), run, "x", "if ($a) {")); // its marker later
        Assert.False(ScriptCode.CheckThenMarker(Script(ran + "\n" + ran), run, "x", "if ($a) {")); // twice
        Assert.False(ScriptCode.CheckThenMarker(Script(ran), run, "y", "if ($a) {")); // another id
    }

    // ---------------------------------------------------------------- a pinned variable

    /// <summary>
    /// The installer test's patterns are each its one <c>$name = '...'</c> in its code: a second assignment in the code
    /// (in another case, in a block, with a scope, to an index, by a compound operator, as either variable of a multiple
    /// assignment) fails the pin, and so does one to a string in double quotes, while one in a comment does not count.
    /// </summary>
    [Theory]
    [InlineData("$HELPERINJOB = 'nothing'")]
    [InlineData("if ($pw) { $helperInJob = 'nothing' }")]
    [InlineData("$script:helperInJob = 'nothing'")]
    [InlineData("$global:helperInJob = 'nothing'")]
    [InlineData("$private:helperInJob = 'nothing'")]
    [InlineData("$helperInJob[0] = 'x'")]
    [InlineData("$helperInJob, $other = 'x', 'y'")]
    [InlineData("$other, $helperInJob = 'x', 'y'")]
    [InlineData("$helperInJob += '|x'")]
    [InlineData("$helperInJob='x'")]
    public void APinnedVariableIsSetOnceInTheCode(string added)
    {
        string e2e = E2e();
        Assert.Equal("helper runs in a job", ScriptCode.PowerShellVariable(e2e, "helperInJob"));
        Assert.Equal("helper could not start in a job", ScriptCode.PowerShellVariable(e2e, "helperNoJob"));
        Assert.ThrowsAny<XunitException>(() => ScriptCode.PowerShellVariable(Once(e2e, Job, added + Environment.NewLine + Job), "helperInJob"));
        Assert.Equal("helper runs in a job", ScriptCode.PowerShellVariable(Once(e2e, Job, "# " + added + Environment.NewLine + Job), "helperInJob"));
        string quoted = Once(e2e, "$helperInJob = 'helper runs in a job'", "$helperInJob = \"helper runs in a job\"");
        Assert.ThrowsAny<XunitException>(() => ScriptCode.PowerShellVariable(quoted, "helperInJob"));
    }

    // ---------------------------------------------------------------- a pinned Pascal statement and its blocks

    private const string CaveatLog = "Log('After it is put back: ' + Caveat);";

    /// <summary>
    /// The uninstaller logs a caveat only for a row it puts back that has one: the pin holds the log line as a statement of
    /// its own with the head of every block around it. Each copy here changes one of those conditions (the caveat's, the
    /// row's put-back test, the row's end, an earlier branch of the same chain), wraps the line in a branch that never runs,
    /// comments it out, or writes it twice: the old pin, which found the line in the routine, would still find it, and this
    /// one fails, as its heads differ or the line is not there once.
    /// </summary>
    [Theory]
    [InlineData("if Caveat <> '' then", "if Caveat = '' then")]
    [InlineData("if Leaves = '' then", "if Leaves <> '' then")]
    [InlineData("and (What <> '') then", "and (What = '') then")]
    [InlineData("else if Copy(Line, 1, Length(CaveatKey)) = CaveatKey then", "else if Copy(Line, 1, Length(CaveatKey)) = LeavesKey then")]
    [InlineData("for I := 0 to GetArrayLength(Lines) - 1 do", "for I := 0 to 0 do")]
    [InlineData("Log('After it is put back: ' + Caveat);", "if False then begin Log('After it is put back: ' + Caveat); end;")]
    [InlineData("Log('After it is put back: ' + Caveat);", "{ Log('After it is put back: ' + Caveat); }")]
    [InlineData("Log('After it is put back: ' + Caveat);", "Log('After it is put back: ' + Caveat); Log('After it is put back: ' + Caveat);")]
    public void APascalStatementIsPinnedWithTheConditionsOfTheBlocksAroundIt(string find, string replace)
    {
        string code = UninstallerTests.CodeSection();
        string routine = ScriptCode.PascalRoutine(code, "ReadChangeList");
        var real = ScriptCode.PascalBlocksAround(code, "ReadChangeList", CaveatLog);
        Assert.Equal("if Caveat <> '' then", real[^1]);
        string changed = Once(code, routine, Once(routine, find, replace));
        Assert.Contains(CaveatLog, changed); // what the old pin looked for, still there as text
        Assert.NotNull(WhyThePinFails(() => ScriptCode.PascalBlocksAround(changed, "ReadChangeList", CaveatLog).SequenceEqual(real)));
    }

    /// <summary>
    /// How <see cref="ScriptCode.PascalBlocksAround"/> reads the blocks of a routine: a statement of the body has no heads;
    /// each begin ... end, try part, repeat and case else adds the head of the statement that holds it, from its start, each
    /// block before it shown as <c>begin ... end</c>; a case branch's own statement has the case up to its label as its
    /// head; comments out, white space between tokens one space, strings as written, a semicolon at the end aside. Several
    /// statements given together must be consecutive in one list, and a statement may be given with its blocks shown as
    /// <c>begin ... end</c>. The own statement of an if's or a loop's branch (after then, else or do, without a begin) is
    /// not a statement of a list, so a pin names the whole if, for or while instead; a statement only in a comment is not
    /// there.
    /// </summary>
    [Fact]
    public void APascalRoutinesBlocksAreReadAsThePinReadsThem()
    {
        const string code = """
            procedure P;
            var
              I: Integer;
            begin
              A := 1;
              if X then
              begin
                B := 2; { a comment }
              end
              else if Y then
                C := 3
              else
              begin
                D := 4;
              end;
              for I := 0 to 9 do
              begin
                E := 5;
              end;
              while F do
                G := 6;
              try
                H := 7;
              except
                Log('x');
              end;
              case I of
                1: begin J := 8; end;
                2: K := 9;
              else
                L := 10;
              end;
              repeat
                M := 11;
              until N;
              { Q := 12; }
              S := '  {';
            end;
            """;
        IReadOnlyList<string> Around(string statement) => ScriptCode.PascalBlocksAround(code, "P", statement);
        Assert.Empty(Around("A := 1;"));
        Assert.Empty(Around("A := 1"));
        Assert.Equal(["if X then"], Around("B := 2"));
        Assert.Equal(["if X then begin ... end else if Y then C := 3 else"], Around("D := 4"));
        Assert.Equal(["for I := 0 to 9 do"], Around("E := 5"));
        Assert.Empty(Around("while F do G := 6"));
        Assert.Equal(["try"], Around("H := 7"));
        Assert.Equal(["try ... except"], Around("Log('x')"));
        Assert.Equal(["case I of 1:"], Around("J := 8"));
        Assert.Equal(["case I of 1: begin ... end; 2:"], Around("K := 9"));
        Assert.Equal(["case I of 1: begin ... end; 2: K := 9; else"], Around("L := 10"));
        Assert.Equal(["repeat ... until N"], Around("M := 11"));
        Assert.Empty(Around("if X then begin B := 2; end else if Y then C := 3 else begin D := 4; end"));
        Assert.Empty(Around("if X then begin ... end else if Y then C := 3 else begin ... end")); // its blocks shown as begin ... end
        Assert.Empty(Around("A := 1; if X then begin ... end else if Y then C := 3 else begin ... end; for I := 0 to 9 do begin E := 5; end;")); // consecutive
        Assert.Empty(Around("case I of 1: begin ... end; 2: K := 9; else L := 10; end"));
        Assert.Empty(Around("S := '  {'"));
        foreach (string branch in new[] { "C := 3", "G := 6", "Q := 12", "S := ' {'", "if X then begin ... end else if Y then C := 3 else begin D := 5; end", "A := 1; for I := 0 to 9 do begin E := 5; end" })
            Assert.ThrowsAny<XunitException>(() => Around(branch));
        // Every statement of the routine's lists and case branches, in the order they start, with its heads.
        var all = ScriptCode.PascalStatements(code, "P");
        Assert.Equal(["A := 1", "if X then begin B := 2; end else if Y then C := 3 else begin D := 4; end", "B := 2", "D := 4"], all.Take(4).Select(s => s.Statement));
        Assert.Equal(("K := 9", "case I of 1: begin ... end; 2:"), all.Where(s => s.Statement == "K := 9").Select(s => (s.Statement, Assert.Single(s.Heads))).Single());
        Assert.Equal("S := '  {'", all[^1].Statement); // the string as written
    }

    // ---------------------------------------------------------------- the [Code] section

    /// <summary>
    /// The [Code] section starts at its header line: a mention of it in an earlier section's comment (as a [Run] entry's
    /// note might say "[Code] does the rest") is not its start, so the pins do not read that section's lines as code. A
    /// second header, as Inno Setup's compiler takes one from a line of the script, fails, and so does a header not
    /// written <c>[Code]</c> with nothing else on its line; a header made of two lines that the preprocessor joins
    /// fails too, as any line it would join to the next does (<see cref="ALineThePreprocessorWouldJoinToTheNextFails"/>).
    /// </summary>
    [Fact]
    public void TheCodeSectionStartsAtItsHeaderLineNotAtAnEarlierMention()
    {
        string script = Repo("installer", "PoolWatch.iss");
        string real = UninstallerTests.CodeSectionOf(script);
        Assert.StartsWith("[Code]", real);
        var run = Regex.Match(script, @"(?m)^\[Run\]\r?$");
        Assert.True(run.Success, "the script has no [Run] section");
        string mentioned = script[..(run.Index + run.Length)] + Environment.NewLine + "; [Code] holds what runs after these entries." + script[(run.Index + run.Length)..];
        Assert.NotEqual(real, mentioned[mentioned.IndexOf("[Code]", StringComparison.Ordinal)..]); // where a cut at the first mention starts
        Assert.Equal(real, UninstallerTests.CodeSectionOf(mentioned));
        Assert.ThrowsAny<XunitException>(() => UninstallerTests.CodeSectionOf(script.Replace("[Code]", "[Codes]", StringComparison.Ordinal)));
        // A second header of the section before the real one, which would start code that no test reads: in another case,
        // with characters up to #32 before or after it (a tab, a form feed, a vertical tab, #1, #31), or after a CR alone,
        // which ends a line as a CR LF does.
        foreach (string second in new[] { "[Code]", "[code]", " \t[CODE]\t ", "\f[Code]", "[Code]\v", "\x01[cOdE]\x1F" })
        {
            string twice = script[..(run.Index + run.Length)] + Environment.NewLine + second + Environment.NewLine + "procedure Extra; begin end;" + script[(run.Index + run.Length)..];
            Assert.ThrowsAny<XunitException>(() => UninstallerTests.CodeSectionOf(twice));
        }
        string afterACr = script[..(run.Index + run.Length)] + Environment.NewLine + "; a note\r[Code]" + Environment.NewLine + "procedure Extra; begin end;" + script[(run.Index + run.Length)..];
        Assert.ThrowsAny<XunitException>(() => UninstallerTests.CodeSectionOf(afterACr));
        // The one header, written otherwise than "[Code]" with nothing else on its line, fails too.
        var header = Regex.Matches(script, @"(?m)^\[Code\](?=\r?$)");
        Assert.Single(header);
        foreach (string written in new[] { "[code]", "  [Code]", "[Code] ", "\f[Code]" })
            Assert.ThrowsAny<XunitException>(() => UninstallerTests.CodeSectionOf(script[..header[0].Index] + written + script[(header[0].Index + header[0].Length)..]));
    }

    /// <summary>
    /// The setup script's lines are split as Inno Setup reads the file, and the white space around them is taken as
    /// Inno Setup takes it: a line ends at a CR LF, a CR or an LF; its compiler takes the characters up to #32 off a
    /// line's ends (and no other character, such as a no-break space); its preprocessor skips #1 to #32 before a
    /// directive's <c>#</c> and before the <c>//</c> of a line it takes out as a comment, and joins a line to the next
    /// when the line is longer than two characters and ends in a backslash with a character up to #32 just before it.
    /// </summary>
    [Fact]
    public void TheScriptsLinesAreReadAsInnoSetupReadsThem()
    {
        var lines = UninstallerTests.IssLines("a\r\nb\r\rc\nd");
        Assert.Equal(["a", "b", "", "c", "d"], lines.Select(l => l.Text));
        Assert.Equal([0, 3, 5, 6, 8], lines.Select(l => l.Start));
        Assert.Equal(["x", ""], UninstallerTests.IssLines("x\r\n\n").Select(l => l.Text));
        Assert.Equal("[Code]", UninstallerTests.IsccTrim("\x01\t\f\v [Code] \x1F\x20"));
        Assert.Equal("\u00A0[Code]", UninstallerTests.IsccTrim("\u00A0[Code]"));
        foreach (string directive in new[] { "#include \"x.iss\"", "  #define X", "\t#if X", "\f#pragma option", "\v#emit", "\x01#endif", "\x1F # sub" })
            Assert.True(UninstallerTests.IsDirectiveLine(directive), $"\"{directive}\" is not read as a directive line");
        foreach (string line in new[] { "x #define", "; #include", "{#AppVersion}", "\u00A0#if" })
            Assert.False(UninstallerTests.IsDirectiveLine(line), $"\"{line}\" is read as a directive line");
        foreach (string comment in new[] { "// x", "  //", "\f// }", "\x01\t//" })
            Assert.True(UninstallerTests.IsPreprocessorComment(comment), $"\"{comment}\" is not read as a line ISPP takes out");
        foreach (string line in new[] { "x // y", "/ /", "{ // }", "\u00A0//" })
            Assert.False(UninstallerTests.IsPreprocessorComment(line), $"\"{line}\" is read as a line ISPP takes out");
        foreach (string joined in new[] { "a \\", "  \\", "[Code]\t\\", "x\x01\\", "x\x1F\\", "// a note \\" })
            Assert.True(UninstallerTests.JoinsTheNextLine(joined), $"\"{joined}\" is not read as a line ISPP joins to the next");
        foreach (string line in new[] { " \\", "\\", "x\\", "C:\\", "a \\ ", "a \\\\", "a\u00A0\\", "a /" })
            Assert.False(UninstallerTests.JoinsTheNextLine(line), $"\"{line}\" is read as a line ISPP joins to the next");
    }

    /// <summary>
    /// A small setup script: a [Setup] section, then its [Code] header (line 4) and the lines <paramref name="code"/>,
    /// each line ending in a CR LF.
    /// </summary>
    private static string SmallScript(params string[] code) =>
        string.Join("\r\n", new[] { "[Setup]", "AppName=X", "", "[Code]" }.Concat(code)) + "\r\n";

    /// <summary>
    /// A line of the setup script that Inno Setup's preprocessor would join to the next (one longer than two characters
    /// that ends in white space and a backslash), in [Code] or before it, the script's first and last lines too, fails
    /// the read of [Code] (<see cref="UninstallerTests.CodeSectionOf"/>): joined, a <c>//</c> comment would take in the
    /// next line, and two lines could make a [Code] header. A line that ends in a backslash with no white space before
    /// it is not joined, in [Code] or before it, nor is a line of two characters, a space and a backslash.
    /// </summary>
    [Fact]
    public void ALineThePreprocessorWouldJoinToTheNextFails()
    {
        Assert.Equal("[Code]\r\nprocedure P;\r\nbegin\r\nend;\r\n", UninstallerTests.CodeSectionOf(SmallScript("procedure P;", "begin", "end;")));
        Assert.Equal("[Code]\r\nconst Folder = 'C:\\'; // in C:\\\r\n",
            UninstallerTests.CodeSectionOf("[Setup]\r\nDefaultDirName=C:\\\r\n[Code]\r\nconst Folder = 'C:\\'; // in C:\\\r\n"));
        Assert.Equal("[Code]\r\n{\r\n \\\r\n}\r\n", UninstallerTests.CodeSectionOf(SmallScript("{", " \\", "}")));
        foreach (string script in new[]
        {
            "[Code] \\\r\n\r\nprocedure Q; begin end;\r\n[Code]\r\nprocedure P; begin end;\r\n",
            "[Setup]\r\nAppName=X \\\r\n[Code]\r\nprocedure P; begin end;\r\n",
            "[Setup]\r\n[Code] \\\r\n\r\nprocedure P; begin end;\r\n[Code]\r\nprocedure Q; begin end;\r\n",
            SmallScript("var A: Boolean; // a note \\", "procedure P; begin end;"),
            SmallScript("procedure P; begin end;\t\\"),
        })
            Assert.ThrowsAny<XunitException>(() => UninstallerTests.CodeSectionOf(script));
    }

    /// <summary>
    /// The lines of [Code] that Inno Setup's preprocessor does not pass on as written are found, each with its number in
    /// the script: one with an inline directive (anywhere on the line), a directive line, and a line it takes out as a
    /// comment of its own, the first line after the header and the script's last too. A line of [Code] with a <c>#</c> or
    /// a <c>//</c> only after its start, and no <c>{#</c>, is not one of them, nor is a directive line before [Code].
    /// </summary>
    [Fact]
    public void TheLinesOfTheCodeThatThePreprocessorChangesAreFound()
    {
        Assert.Empty(UninstallerTests.CodeLinesThePreprocessorChanges(SmallScript("var A: String; // a note", "{ #1 and // in a comment }", "procedure P; begin end;")));
        Assert.Empty(UninstallerTests.CodeLinesThePreprocessorChanges("#define X\r\n[Setup]\r\n[Code]\r\nprocedure P; begin end;\r\n"));
        Assert.Equal(
        [
            "line 5, an inline directive: const V = '{#AppVersion}';",
            "line 6, a directive line:   #define X",
            "line 7, a line the preprocessor takes out as a comment: \t// a note",
            "line 9, a line the preprocessor takes out as a comment: // last",
        ], UninstallerTests.CodeLinesThePreprocessorChanges(SmallScript("const V = '{#AppVersion}';", "  #define X", "\t// a note", "procedure P; begin end;", "// last")));
    }

    /// <summary>
    /// Every line after the [Code] header, to the end of the script, that Inno Setup's compiler takes for a section's
    /// header or its preprocessor for a directive is found, in a comment too, and after a line that starts another
    /// section as well, the first line after the header and the script's last too; a line before [Code] is not one of
    /// them, nor is a list in brackets that opens on a line of code.
    /// </summary>
    [Fact]
    public void EveryLineOfTheCodeTakenForAHeaderOrADirectiveIsFound()
    {
        Assert.Empty(InstallerPagesTests.CodeLinesTakenForAHeaderOrADirective(SmallScript("procedure P;", "begin", "  Log(Format('%d', [1]));", "end;")));
        Assert.Equal(
        [
            "line 7: [Notes]",
            "line 9: \t[Run]",
            "line 10:   #if X",
            "line 11: [/Code]",
        ], InstallerPagesTests.CodeLinesTakenForAHeaderOrADirective(SmallScript("procedure P; begin end;", "{", "[Notes]", "}", "\t[Run]", "  #if X", "[/Code]", "x")));
        Assert.Equal(
        [
            "line 5: #if X",
            "line 8: [Notes]",
            "line 10: \t[Run]",
            "line 11: [/Code]",
        ], InstallerPagesTests.CodeLinesTakenForAHeaderOrADirective(SmallScript("#if X", "procedure P; begin end;", "{", "[Notes]", "}", "\t[Run]", "[/Code]")));
    }

    // ---------------------------------------------------------------- the PowerShell reader itself

    [Fact]
    public void PowerShellStatementsAreGroupedAsTheReaderGroupsThem()
    {
        string script = string.Join("\n",
            "$a = 1; $b = 2",
            "if ($a) {",
            "    'x'",
            "}",
            "else { 'y' }",
            "$c = $a -and",
            "    $b",
            "Get-Thing |",
            "    Where-Object { $_ }",
            "Get-Other `",
            "    -Force",
            "do { $a-- }",
            "while ($a -gt 0)",
            "Write-Host \"# not a comment $( '}' ) (\" # a comment",
            "$h = @'",
            "}{ # ' \"",
            "'@",
            "try { 1 }",
            "catch { 2 }",
            "function F($x) { $x }");
        var statements = ScriptCode.Statements(script).Select(OneLine).ToList();
        Assert.Equal(
        [
            "$a = 1", "$b = 2", "if ($a) { 'x' } else { 'y' }", "$c = $a -and $b", "Get-Thing | Where-Object { $_ }", "Get-Other ` -Force",
            "do { $a-- } while ($a -gt 0)", "Write-Host \"# not a comment $( '}' ) (\"", "$h = @' }{ # ' \" '@", "try { 1 } catch { 2 }", "function F($x) { $x }",
        ], statements);
        Assert.Equal(" 'x' ", ScriptCode.Block("if ($a) { 'x' } else { 'y' }"));
        Assert.Equal(" $x ", ScriptCode.Block("function F($x) { $x }"));
        Assert.Equal("Write-Host \"# not a comment $( '}' ) (\" ", ScriptCode.PowerShell("Write-Host \"# not a comment $( '}' ) (\" # a comment"));
        Assert.Equal("a   b", ScriptCode.PowerShell("a <# x\ny #> b")); // a block comment leaves a space
        Assert.Equal(["$x = 1"], ScriptCode.StatementsOf("if ($a) {\n  if ($b) {\n    $x = 1\n  }\n}", "if ($a) {", "if ($b) {"));
        Assert.Null(ScriptCode.StatementsOf("if ($a) { 1 }\nif ($a) { 2 }", "if ($a) {")); // the block not there once
    }
}
