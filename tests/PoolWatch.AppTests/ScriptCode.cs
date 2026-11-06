using System.Text;
using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// What the tests' text pins read of a script or a source file. A pin that reads a text as written is met by a copy of the
/// pinned code in a comment, and holds a line whatever block or condition it is under. So the pins read:
/// <list type="bullet">
/// <item>the code without its comments (<see cref="PowerShell"/>, <see cref="Pascal"/>, <see cref="CSharp"/>; in Pascal each
/// comment leaves a bare <c>{}</c>; in C# each string and character is left empty too, and so is the code of every
/// <c>#if</c>, each branch of it, as a build may leave it out);</item>
/// <item>a definition only where it is code, and only when it is the only one of its name there
/// (<see cref="PowerShellFunction"/>, <see cref="PascalRoutine"/>, <see cref="CSharpMember"/>, which also fails on a member
/// with an <c>#if</c> in it), whole, from its header to its own end; the names of all the routines Pascal code defines
/// (<see cref="PascalRoutineNames"/>) and the code outside them (<see cref="PascalOutsideRoutines"/>), and the tokens of
/// C# code (<see cref="CSharpTokens"/>), by which a test finds the pins of a test file;</item>
/// <item>statements in their blocks (<see cref="StatementsOf"/> in PowerShell, with <see cref="Head"/> and
/// <see cref="StringList"/> for the list a loop goes over, and <see cref="PowerShellAssignments"/> for the assignments of
/// a variable; <see cref="PascalBlocksAround"/> and <see cref="PascalStatements"/> in Pascal, which read each statement as
/// tokens, its strings as written), so a pin can hold a check as a whole statement of its own in a named block, a loop's
/// list as strings and nothing else, in the loop's head or in the only assignment of its variable, and in Pascal a
/// statement, or a run of them, with the conditions of the blocks around it, or all that a block holds.</item>
/// </list>
/// So a pin that compares a definition whole, or holds a statement in its block, fails when what it pins is edited,
/// removed, commented out, put in a string, or moved into another block or under another condition. Three limits: an
/// edit that only changes how long a run of white space is, or which white space it is, can leave a pin green (most
/// pins make each run one space, some inside strings too); a Pascal statement given with a block in it shown as
/// <c>begin ... end</c> does not compare what that block holds, so the tests hold the statements of each such block
/// with pins of their own; and <see cref="CSharpMember"/> finds a member wherever it is in its file's code, so a member
/// moved into another type leaves its pin green. A test that only finds a text in a definition's code (the setup
/// script's tests count calls that way) holds that it is there, not where. In the setup script's [Code], though, every
/// routine is also held whole, as written, its comments included, from its header to its own last <c>end;</c> (an
/// <c>external</c> declaration to the semicolon after its string), with every run of white space made one space
/// (<see cref="UninstallerTests.EveryRoutineOfTheScriptIsHeldWhole"/>, which fails when a routine has no such pin): so an
/// edit to a routine fails a pin there unless it only changes how long a run of white space is, or which white space it
/// is. The readers take [Code]'s comments and strings as Pascal Script takes them (<see cref="Pascal"/>), split the
/// script's lines as Inno Setup reads the file (<see cref="UninstallerTests.IssLines"/>) and take the white space at
/// their ends as its compiler does (<see cref="UninstallerTests.IsccTrim"/>); no line of the script longer than two
/// characters may end in white space (a character up to #32) and a backslash, which Inno Setup's preprocessor joins to
/// the next (<see cref="UninstallerTests.CodeSectionOf"/>, with <see cref="UninstallerTests.JoinsTheNextLine"/>: the
/// preprocessor's span symbol is #0 until a <c>#pragma spansymbol</c> sets it, and the ISPPBuiltins.iss it reads in
/// before every script, from Inno Setup's own folder, makes it a backslash), so it joins no two of its lines. Outside
/// its routines [Code] may hold only comments and plain declarations of constants and variables, and no text of it may
/// start a compiler directive (<c>{$</c>, <c>(*$</c>)
/// (<see cref="UninstallerTests.OutsideItsRoutinesTheCodeHasOnlyPlainConstantsAndVariables"/>); Inno Setup's
/// preprocessor passes it on as it is (<see cref="UninstallerTests.ThePreprocessorPutsNothingIntoTheCode"/>: it joins
/// no line, and [Code] has no <c>{#</c>, no directive line and no line it takes out as a comment of its own); no line
/// of it starts a section (<see cref="InstallerPagesTests.NoLineOfTheCodeBeginsWithABracketOrAHash"/>); and the
/// declarations are held only where a test reads them. No read tells whether a line runs: code elsewhere that ends the
/// script or the block before it, or that replaces a pinned definition as the script runs (in [Code], code added later
/// is an edit to a routine, a new routine, or text between the routines, such as an event attribute: the first two fail
/// a test, and so does the third unless it is a plain declaration of a constant or a variable or a comment that holds
/// none of the texts above; a parameter of the script's other sections that calls a routine, such as Check: or
/// {code:...}, is held only where a test reads it), leaves the pin green. For the checks of the CI scripts the markers
/// tell that: each such check a pin holds prints <c>CHECK-RAN: &lt;id&gt;</c> in its own block as the statement right
/// after it or right after the last check of the run of checks it is in, or as the statement right after the try
/// statement (with a finally and no catch) whose body holds it (a marker anywhere else does not count). Each test that
/// pins such a check holds it to its marker: <see cref="CheckThenMarker"/> holds the first two (given one check, the
/// marker right after it; given a run of checks, the marker right after its last; <see cref="OnlyCheckThenMarker"/> a
/// loop whose body is the check and its marker alone), <see cref="UninstallerTests.ChecksWithoutAMarker"/> all three
/// for each statement it reads that starts with Assert-True and a space, reading the blocks by their braces
/// (<see cref="Statements"/>, <see cref="Blocks"/>) and not what is inside brackets. And in CI a step after the one
/// that runs the script fails the job when the script's output lacks one that <c>ci/expected-checks.txt</c> lists for
/// the script (for the soak, on its ui leg, the one that runs the pinned checks) or, on every leg, the line
/// Complete-Assertions prints when no check failed (<see cref="CheckMarkerTests"/>). Neither layer checks the data a
/// check reads. The readers are not full parsers: they split the scripts of this repository as these tests need
/// (<see cref="ScriptCodeTests"/>).
/// </summary>
internal static class ScriptCode
{
    /// <summary>A definition the pins compare: its whole text (<see cref="Value"/>, as Regex's Match names it).</summary>
    internal readonly record struct Found(string Value);

    private enum Kind { Space, Newline, Comment, String, Word, Symbol, Directive }

    private readonly record struct Token(Kind Kind, int Start, int End);

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    // ------------------------------------------------------------------------------------------------ PowerShell

    /// <summary>The script's code: each comment taken out (a block comment left as one space), the rest as written.</summary>
    public static string PowerShell(string script) => new PsCode(script).WithoutComments();

    /// <summary>
    /// The statements of PowerShell code (a script's code, or the inside of a block), its top level only, in order, each as
    /// written. A statement ends at a new line or a semicolon outside brackets, except where the line goes on: after a
    /// backtick, a pipe, a comma, an equals sign or an operator, before a line that starts with a pipe, and before the else,
    /// elseif, catch or finally of the statement, or the while or until of a do loop.
    /// </summary>
    public static IReadOnlyList<string> Statements(string code) => new PsCode(code).Statements();

    /// <summary>The inside of the statement's first block outside brackets: the body of an if, a function or a loop.</summary>
    public static string Block(string statement) => new PsCode(statement).FirstBlock();

    /// <summary>
    /// Every block of the statement outside brackets, in order, each with the text between it and the block before it (or
    /// the statement's start): an if's, elseif's and else's, a loop's, a try's, catch's and finally's, a function's, and a
    /// script block given to a command (as ForEach-Object's); a hashtable's braces outside brackets are read as one too. A
    /// block inside brackets (a script block in a check's parentheses) is not one of them.
    /// </summary>
    public static IReadOnlyList<(string Before, string Inside)> Blocks(string statement) => new PsCode(statement).Blocks();

    /// <summary>
    /// The statement's head: its text before its first block outside brackets (an if's or a loop's condition, a function's
    /// header), with its white space made one space.
    /// </summary>
    public static string Head(string statement) => OneLine(new PsCode(statement).BeforeFirstBlock());

    /// <summary>
    /// The strings of <paramref name="text"/> when it is strings in single quotes separated by commas and nothing else,
    /// each as it reads (a doubled quote one quote); null when it is anything more (a variable, a call, a pipe after the
    /// list, an index into it).
    /// </summary>
    public static IReadOnlyList<string>? StringList(string text)
    {
        var list = Regex.Match(text.Trim(), @"^'((?:[^']|'')*)'(?:\s*,\s*'((?:[^']|'')*)')*$");
        if (!list.Success) return null;
        return new[] { list.Groups[1].Value }.Concat(list.Groups[2].Captures.Select(c => c.Value)).Select(s => s.Replace("''", "'", StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// The statements of the block that <paramref name="blocks"/> lead to in the script's code (<see cref="PowerShell"/>),
    /// each with its white space made one space: each names the start of the one statement, in the block before it, whose
    /// first block is the next (none: the script's top level). Null when a block on the way is not there exactly once.
    /// </summary>
    public static IReadOnlyList<string>? StatementsOf(string script, params string[] blocks)
    {
        var statements = Statements(PowerShell(script));
        foreach (string block in blocks)
        {
            var opening = statements.Where(s => OneLine(s).StartsWith(block, StringComparison.Ordinal)).ToList();
            if (opening.Count != 1) return null;
            statements = Statements(Block(opening[0]));
        }
        return statements.Select(OneLine).ToList();
    }

    /// <summary>The statements of <see cref="StatementsOf"/> that start with <paramref name="start"/>; none when it is null.</summary>
    public static IReadOnlyList<string> StatementsIn(string script, string start, params string[] blocks) =>
        StatementsOf(script, blocks)?.Where(s => s.StartsWith(start, StringComparison.Ordinal)).ToList() ?? [];

    /// <summary>
    /// The statement by which a CI script says that a check the tests pin has run: <c>CHECK-RAN: &lt;id&gt;</c>, which CI
    /// looks for in the script's output (<c>ci/expected-checks.txt</c>, <see cref="CheckMarkerTests"/>).
    /// </summary>
    public static string Marker(string id) => $"Write-Host 'CHECK-RAN: {id}'";

    /// <summary>
    /// Whether the block that <paramref name="blocks"/> lead to (<see cref="StatementsOf"/>) holds exactly one statement that
    /// starts with <paramref name="check"/>, and the statement right after it is the <see cref="Marker"/> of
    /// <paramref name="id"/>: a check of its own in that block, and its marker next.
    /// </summary>
    public static bool CheckThenMarker(string script, string check, string id, params string[] blocks) => CheckThenMarker(script, [check], id, blocks);

    /// <summary>
    /// Whether the block that <paramref name="blocks"/> lead to (<see cref="StatementsOf"/>) holds exactly one statement that
    /// starts with the last of <paramref name="run"/>, the statements right before it start with the others, in order, and
    /// the statement right after it is the <see cref="Marker"/> of <paramref name="id"/>: a run of checks in that block, its
    /// last a check of its own there, and their marker right after the last.
    /// </summary>
    public static bool CheckThenMarker(string script, IReadOnlyList<string> run, string id, params string[] blocks)
    {
        var statements = StatementsOf(script, blocks);
        if (statements is null || run.Count == 0) return false;
        var at = Enumerable.Range(0, statements.Count).Where(i => statements[i].StartsWith(run[^1], StringComparison.Ordinal)).ToList();
        if (at.Count != 1 || at[0] + 1 >= statements.Count || statements[at[0] + 1] != Marker(id)) return false;
        int first = at[0] - (run.Count - 1);
        return first >= 0 && Enumerable.Range(0, run.Count - 1).All(k => statements[first + k].StartsWith(run[k], StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the block that <paramref name="blocks"/> lead to holds exactly two statements: one that starts with
    /// <paramref name="check"/>, then the <see cref="Marker"/> of <paramref name="id"/>. For a loop's check: no other
    /// statement of the loop runs before the check or after its marker in any round, so none can skip the check of a later
    /// round (a <c>continue</c> before it, a <c>break</c> after the marker).
    /// </summary>
    public static bool OnlyCheckThenMarker(string script, string check, string id, params string[] blocks) =>
        StatementsOf(script, blocks) is [string first, string second] && first.StartsWith(check, StringComparison.Ordinal) && second == Marker(id);

    /// <summary>
    /// How many times the script's code (<see cref="PowerShell"/>) defines the function <paramref name="name"/>, as a
    /// function, a filter or a workflow, in any case, with or without a scope, at any depth.
    /// </summary>
    public static int PowerShellDefinitions(string script, string name) => new PsCode(PowerShell(script)).Definitions(name);

    /// <summary>
    /// The one definition of the function <paramref name="name"/> in the script's code (<see cref="PowerShell"/>): fails
    /// unless the code defines it (as a function or a filter, in any case, with or without a scope) exactly once, as a
    /// statement of its top level. Its whole statement, header to the closing brace, without its comments.
    /// </summary>
    public static Found PowerShellFunction(string script, string name)
    {
        var code = new PsCode(PowerShell(script));
        int definitions = code.Definitions(name);
        Assert.True(definitions == 1, $"the script defines {name} {definitions} times, not once, in its code");
        var header = new Regex($@"^(function|filter)\s+((script|global|local|private):)?{Regex.Escape(name)}(?![\w-])", RegexOptions.IgnoreCase);
        var top = code.Statements().Where(s => header.IsMatch(s)).ToList();
        Assert.True(top.Count == 1, $"{name} is not defined by a statement of the script's top level");
        return new Found(top[0]);
    }

    /// <summary>
    /// How many times the script's code (<see cref="PowerShell"/>, its strings included) assigns the variable
    /// <paramref name="name"/> (without its <c>$</c>) as <c>$name =</c>: in any case, with or without a scope
    /// (<c>$script:</c>, <c>$global:</c>, <c>$local:</c> or <c>$private:</c>), in braces or not (<c>${name} =</c>,
    /// <c>${script:name} =</c>), with an index (<c>$name[0] =</c>) or a compound assignment's operator (<c>+=</c> and the
    /// like), and as one of the variables of a multiple assignment (<c>$name, $other = ...</c>). A variable set another
    /// way (by Set-Variable, New-Variable or as a loop's variable, say) is not counted.
    /// </summary>
    public static int PowerShellAssignments(string script, string name)
    {
        const string index = @"(\s*\[[^\]]*\])*", scope = "((script|global|local|private):)?";
        string variable = $@"(\${scope}{Regex.Escape(name)}|\$\{{{scope}{Regex.Escape(name)}\}})";
        return Regex.Matches(PowerShell(script), $@"(?<![\w$:{{])(?<![\w$:]\{{){variable}{index}\s*(,\s*\$([\w:]+|\{{[^}}]+\}}){index}\s*)*([-+*/%]|\?\?)?=(?!=)",
            RegexOptions.IgnoreCase).Count;
    }

    /// <summary>
    /// The string in single quotes that the script sets the variable <paramref name="name"/> (without its <c>$</c>) to,
    /// read in its code (<see cref="PowerShell"/>): fails unless the code assigns it once
    /// (<see cref="PowerShellAssignments"/>), by a statement of its top level that is <c>$name = '...'</c>. Other ways of
    /// setting a variable are not read.
    /// </summary>
    public static string PowerShellVariable(string script, string name)
    {
        string code = PowerShell(script);
        int sets = PowerShellAssignments(script, name);
        Assert.True(sets == 1, $"the script's code sets ${name} {sets} times, not once");
        var found = Statements(code).Where(s => Regex.IsMatch(s, $@"^\$((script|global|local|private):)?{Regex.Escape(name)}\s*=", RegexOptions.IgnoreCase)).ToList();
        Assert.True(found.Count == 1, $"${name} is not set by a statement of the script's top level");
        var value = Regex.Match(found[0], $@"^\${Regex.Escape(name)} = '([^']*)'$");
        Assert.True(value.Success, $"${name} is not set to a string in single quotes: {found[0]}");
        return value.Groups[1].Value;
    }

    /// <summary>PowerShell's tokens, the code's brackets matched, and the statements of its top level.</summary>
    private sealed class PsCode
    {
        private static readonly HashSet<string> Operators = BuildOperators();

        private readonly string _s;
        private readonly List<Token> _t;
        /// <summary>The tokens that are not white space or comments (new lines are kept: they end statements).</summary>
        private readonly List<int> _sig;
        /// <summary>For an opening bracket in <see cref="_sig"/>, the place of its closing one; else -1.</summary>
        private readonly int[] _close;

        public PsCode(string s)
        {
            _s = s;
            _t = [];
            int i = 0;
            Lex(s, ref i, _t, untilParen: false);
            _sig = Enumerable.Range(0, _t.Count).Where(k => _t[k].Kind is not (Kind.Space or Kind.Comment)).ToList();
            _close = Brackets(_sig.Count, k => Sym(k));
        }

        private static HashSet<string> BuildOperators()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-and", "-or", "-xor", "-not", "-band", "-bor", "-bxor", "-bnot", "-shl", "-shr", "-f", "-join", "-split", "-replace",
                "-is", "-isnot", "-as", "-",
            };
            foreach (var op in new[] { "eq", "ne", "gt", "ge", "lt", "le", "like", "notlike", "match", "notmatch", "contains", "notcontains", "in", "notin", "replace", "split" })
            {
                set.Add("-" + op);
                set.Add("-c" + op);
                set.Add("-i" + op);
            }
            return set;
        }

        private string Text(int k) => _s[_t[_sig[k]].Start.._t[_sig[k]].End];
        private bool IsNewline(int k) => _t[_sig[k]].Kind == Kind.Newline;
        private string? Sym(int k) => _t[_sig[k]].Kind == Kind.Symbol ? Text(k) : null;
        private bool IsSym(int k, string s) => k >= 0 && k < _sig.Count && Sym(k) == s;
        private bool IsWord(int k, params string[] words) =>
            k >= 0 && k < _sig.Count && _t[_sig[k]].Kind == Kind.Word && words.Contains(Text(k), StringComparer.OrdinalIgnoreCase);

        /// <summary>The next token after <paramref name="k"/> that is not a new line; -1 at the end.</summary>
        private int Next(int k)
        {
            for (int j = k + 1; j < _sig.Count; j++) if (!IsNewline(j)) return j;
            return -1;
        }

        /// <summary>Where each comment token is: its start and its end, its opening and closing included.</summary>
        public IReadOnlyList<(int Start, int End)> CommentSpans() => _t.Where(t => t.Kind == Kind.Comment).Select(t => (t.Start, t.End)).ToList();

        public string WithoutComments()
        {
            var sb = new StringBuilder();
            foreach (var t in _t)
            {
                if (t.Kind == Kind.Comment) sb.Append(_s[t.Start] == '<' ? " " : "");
                else sb.Append(_s, t.Start, t.End - t.Start);
            }
            return sb.ToString();
        }

        /// <summary>Whether the statement that began at <paramref name="first"/> goes on past the new line at <paramref name="newline"/>.</summary>
        private bool Continues(int first, int last, int newline)
        {
            if (Sym(last) is "|" or "," or "=" or "+" or "-" or "*" or "/" or "%" or "!") return true;
            if (_t[_sig[last]].Kind == Kind.Word && Operators.Contains(Text(last))) return true;
            int next = Next(newline);
            if (next < 0) return false;
            if (IsSym(next, "|") || IsWord(next, "else", "elseif", "catch", "finally")) return true;
            return IsWord(first, "do") && IsWord(next, "while", "until");
        }

        /// <summary>The top-level statements, each as written (see <see cref="ScriptCode.Statements"/>).</summary>
        public IReadOnlyList<string> Statements()
        {
            var list = new List<string>();
            int first = -1, last = -1;
            for (int k = 0; k < _sig.Count; k++)
            {
                bool newline = IsNewline(k);
                if (newline || IsSym(k, ";"))
                {
                    if (first < 0 || newline && Continues(first, last, k)) continue;
                    list.Add(_s[_t[_sig[first]].Start.._t[_sig[last]].End]);
                    first = -1;
                    continue;
                }
                if (first < 0) first = k;
                if (_close[k] > k) k = _close[k];
                last = k;
            }
            if (first >= 0) list.Add(_s[_t[_sig[first]].Start.._t[_sig[last]].End]);
            return list;
        }

        public string FirstBlock()
        {
            int k = FirstBrace();
            return _s[_t[_sig[k]].End.._t[_sig[_close[k]]].Start];
        }

        public string BeforeFirstBlock() => _s[.._t[_sig[FirstBrace()]].Start];

        public IReadOnlyList<(string Before, string Inside)> Blocks()
        {
            var list = new List<(string, string)>();
            int after = 0; // where the text after the last block found starts
            for (int k = 0; k < _sig.Count; k++)
            {
                if (_close[k] <= k) continue;
                if (IsSym(k, "{"))
                {
                    list.Add((_s[after.._t[_sig[k]].Start], _s[_t[_sig[k]].End.._t[_sig[_close[k]]].Start]));
                    after = _t[_sig[_close[k]]].End;
                }
                k = _close[k]; // a block's or a bracket's inside is not read here
            }
            return list;
        }

        /// <summary>The opening brace of the statement's first block outside brackets.</summary>
        private int FirstBrace()
        {
            for (int k = 0; k < _sig.Count; k++)
            {
                if (IsSym(k, "{") && _close[k] > k) return k;
                if (_close[k] > k) k = _close[k];
            }
            Assert.Fail("the statement has no block: " + _s);
            return -1;
        }

        /// <summary>How many times the code defines the function <paramref name="name"/>, at any depth, with or without a scope.</summary>
        public int Definitions(string name)
        {
            int n = 0;
            for (int k = 0; k + 1 < _sig.Count; k++)
            {
                if (!IsWord(k, "function", "filter", "workflow")) continue;
                int next = Next(k);
                if (next >= 0 && _t[_sig[next]].Kind == Kind.Word && Text(next)[(Text(next).LastIndexOf(':') + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        private static bool WordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '\\' or '/' or '*' or '?' or '~';

        /// <summary>
        /// PowerShell's tokens from <paramref name="i"/>: white space (a backtick at a line's end too), new lines, comments
        /// (<c>#</c> to the line's end, <c>&lt;# #&gt;</c>), strings (single and double quoted, here-strings, with the
        /// subexpressions in double-quoted ones), variables and words, and single-character symbols. With
        /// <paramref name="untilParen"/>, it stops after the bracket that closes a subexpression.
        /// </summary>
        private static void Lex(string s, ref int i, List<Token>? list, bool untilParen)
        {
            int depth = 0, n = s.Length;
            while (i < n)
            {
                int start = i;
                char c = s[i];
                Kind kind;
                if (c is '\r' or '\n')
                {
                    i += c == '\r' && At(s, i + 1, '\n') ? 2 : 1;
                    kind = Kind.Newline;
                }
                else if (c is ' ' or '\t' or '\f' or '\v' || c == '`' && i + 1 < n && s[i + 1] is '\r' or '\n')
                {
                    while (i < n)
                    {
                        if (s[i] is ' ' or '\t' or '\f' or '\v') i++;
                        else if (s[i] == '`' && i + 1 < n && s[i + 1] is '\r' or '\n') i += s[i + 1] == '\r' && At(s, i + 2, '\n') ? 3 : 2;
                        else break;
                    }
                    kind = Kind.Space;
                }
                else if (c == '<' && At(s, i + 1, '#'))
                {
                    int e = s.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    kind = Kind.Comment;
                }
                else if (c == '#' && (i == 0 || !WordChar(s[i - 1])))
                {
                    while (i < n && s[i] is not ('\r' or '\n')) i++;
                    kind = Kind.Comment;
                }
                else if (c == '@' && i + 1 < n && s[i + 1] is '\'' or '"' && BlankToLineEnd(s, i + 2))
                {
                    char q = s[i + 1];
                    int e = i + 2;
                    while (true)
                    {
                        int nl = s.IndexOf('\n', e);
                        if (nl < 0) { i = n; break; }
                        if (At(s, nl + 1, q) && At(s, nl + 2, '@')) { i = nl + 3; break; }
                        e = nl + 1;
                    }
                    kind = Kind.String;
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < n)
                    {
                        if (s[i] == '\'') { if (At(s, i + 1, '\'')) { i += 2; continue; } i++; break; }
                        i++;
                    }
                    kind = Kind.String;
                }
                else if (c == '"')
                {
                    i = DoubleQuoted(s, i);
                    kind = Kind.String;
                }
                else if (c == '$' && At(s, i + 1, '{'))
                {
                    int e = s.IndexOf('}', i + 2);
                    i = e < 0 ? n : e + 1;
                    kind = Kind.Word;
                }
                else if (c == '$' && i + 1 < n && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] is '_' or '?' or '^' or '$'))
                {
                    i++;
                    if (s[i] is '?' or '^' or '$') i++;
                    else while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or ':')) i++;
                    kind = Kind.Word;
                }
                else if (WordChar(c))
                {
                    while (i < n && WordChar(s[i])) i++;
                    kind = Kind.Word;
                }
                else
                {
                    i++;
                    kind = Kind.Symbol;
                    if (untilParen && c == '(') depth++;
                    else if (untilParen && c == ')' && depth-- == 0) { list?.Add(new Token(kind, start, i)); return; }
                }
                list?.Add(new Token(kind, start, i));
            }
        }

        /// <summary>The index after the double-quoted string at <paramref name="i"/>: backtick escapes, doubled quotes and <c>$( )</c> skipped.</summary>
        private static int DoubleQuoted(string s, int i)
        {
            int n = s.Length;
            i++;
            while (i < n)
            {
                char c = s[i];
                if (c == '`') { i += 2; continue; }
                if (c == '"') { if (At(s, i + 1, '"')) { i += 2; continue; } return i + 1; }
                if (c == '$' && At(s, i + 1, '(')) { i += 2; Lex(s, ref i, null, untilParen: true); continue; }
                i++;
            }
            return n;
        }

        private static bool BlankToLineEnd(string s, int i)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            return i >= s.Length || s[i] is '\r' or '\n';
        }
    }

    // ------------------------------------------------------------------------------------------------ Pascal (Inno Setup)

    /// <summary>
    /// Pascal code (an Inno Setup [Code] section, or a part of one) with each comment a bare <c>{}</c>, the rest as written.
    /// Every Pascal read here takes comments and strings as Pascal Script does: a comment from <c>{</c> to the next
    /// <c>}</c>, from <c>(*</c> to the next <c>*)</c> that starts at its <c>*</c> or later (so <c>(*)</c> is a whole
    /// comment), or from <c>//</c> to the line's end, and a string from <c>'</c> to the next lone <c>'</c> or the line's
    /// end; a line ends at a CR or an LF. No other token, of the readers or of Pascal Script, holds a <c>{</c>, a
    /// <c>(*</c>, a <c>//</c> or a quote, so the readers find the comments and strings of the code where Pascal Script
    /// finds them. White space is spaces, tabs, form feeds, vertical tabs and line ends (Pascal Script refuses a form feed
    /// or a vertical tab outside a comment or a string). Pascal Script reads the setup script's [Code] as Inno Setup's
    /// preprocessor passes it on, which joins none of its lines and changes none of them
    /// (<see cref="UninstallerTests.ThePreprocessorPutsNothingIntoTheCode"/>), so the comments and strings it finds there
    /// are the ones found here.
    /// </summary>
    public static string Pascal(string code) => new PascalCode(code).WithoutComments();

    /// <summary>
    /// The one function or procedure <paramref name="name"/> of the Pascal code, as written (its comments too): fails unless
    /// the code (not a comment or a string) has exactly one header for it. From its header to the <c>end;</c> of its own
    /// begin; for a routine Windows provides, to the semicolon after its <c>external '...'</c>.
    /// </summary>
    public static string PascalRoutine(string code, string name) => new PascalCode(code).Routine(name);

    /// <summary>
    /// The code of the Pascal code outside its routines, each routine taken out from its header to its own last token, as
    /// <see cref="PascalRoutine"/> reads it (a header as <see cref="PascalRoutineNames"/> finds one): every other token of the
    /// code, in order, each as written, and none of its comments.
    /// </summary>
    public static IReadOnlyList<string> PascalOutsideRoutines(string code) => new PascalCode(code).OutsideRoutines();

    /// <summary>
    /// Where the statements <paramref name="statements"/> (one, or several in order, as one text) are in the routine
    /// <paramref name="name"/> of the Pascal code: fails unless the routine holds them exactly once as consecutive whole
    /// statements of one statement list (a begin ... end, a try's part, a repeat, a case's else), or, for one statement, as
    /// a case branch's own statement. Each is read as Pascal tokens: comments out, white space between tokens made one
    /// space, strings as written, a semicolon at its end left out. A given statement may show the blocks in it as
    /// <c>begin ... end</c>, as the heads show them; then what those blocks hold is not compared. The heads of the blocks
    /// around them, from the routine's body in (none: statements of the body itself): for each, the statement that holds the
    /// block, from its start to the block, read the same way, each block before it in that statement shown as
    /// <c>begin ... end</c> (for a case branch, the case up to the branch's label). So a pin that holds them holds the
    /// statements, their order, and the conditions of every block they are in.
    /// </summary>
    public static IReadOnlyList<string> PascalBlocksAround(string code, string name, string statements)
    {
        var wanted = new PascalCode(statements).Top();
        Assert.True(wanted.Count > 0, "no statement to look for: " + statements);
        var found = new PascalCode(code).Around(name, wanted);
        Assert.True(found.Count == 1, $"{name} holds \"{string.Join("; ", wanted)}\" as {(wanted.Count == 1 ? "a whole statement" : "consecutive whole statements")} of its own {found.Count} times, not once");
        return found[0];
    }

    /// <summary>
    /// Every whole statement of a statement list in the routine <paramref name="name"/> of the Pascal code, and every case
    /// branch's own statement that is not a begin ... end, in the order they start, each read as
    /// <see cref="PascalBlocksAround"/> reads it (in full), with the heads of the blocks it is in: so a pin can hold what a
    /// block (the statements with the same heads) holds, and that nothing like it is outside it.
    /// </summary>
    public static IReadOnlyList<(string Statement, IReadOnlyList<string> Heads)> PascalStatements(string code, string name) => new PascalCode(code).All(name);

    /// <summary>
    /// The names of the functions and procedures the Pascal code defines, in the order of their headers: the word after
    /// each <c>function</c> or <c>procedure</c> of the code (not of a comment or a string), a routine Windows provides
    /// (<c>external ...</c>) included, but not one after a colon or an equals sign (a parameter's or a type's), nor one
    /// that no word follows (as in <c>array of procedure;</c>).
    /// </summary>
    public static IReadOnlyList<string> PascalRoutineNames(string code) => new PascalCode(code).RoutineNames();

    private sealed class PascalCode
    {
        private readonly string _s;
        private readonly List<Token> _t;
        /// <summary>The tokens that are not white space, new lines or comments.</summary>
        private readonly List<int> _sig;
        private readonly int[] _close;

        public PascalCode(string s)
        {
            _s = s;
            _t = Lex(s);
            _sig = Enumerable.Range(0, _t.Count).Where(k => _t[k].Kind is not (Kind.Space or Kind.Newline or Kind.Comment)).ToList();
            _close = Brackets(_sig.Count, k => _t[_sig[k]].Kind == Kind.Symbol ? Text(k) : null);
        }

        private string Text(int k) => _s[_t[_sig[k]].Start.._t[_sig[k]].End];
        private bool IsWord(int k, params string[] words) =>
            k >= 0 && k < _sig.Count && _t[_sig[k]].Kind == Kind.Word && words.Contains(Text(k), StringComparer.OrdinalIgnoreCase);
        private bool IsSym(int k, string s) => k >= 0 && k < _sig.Count && _t[_sig[k]].Kind == Kind.Symbol && Text(k) == s;

        /// <summary>Where each comment token is: its start and its end, its opening and closing included.</summary>
        public IReadOnlyList<(int Start, int End)> CommentSpans() => _t.Where(t => t.Kind == Kind.Comment).Select(t => (t.Start, t.End)).ToList();

        public string WithoutComments()
        {
            var sb = new StringBuilder();
            foreach (var t in _t) sb.Append(t.Kind == Kind.Comment ? "{}" : _s[t.Start..t.End]);
            return sb.ToString();
        }

        /// <summary>
        /// Whether a routine's header starts at <paramref name="k"/>: <c>function</c> or <c>procedure</c>, not after a colon or
        /// an equals sign (a parameter's, a variable's or a type's), followed by a word, its name.
        /// </summary>
        private bool IsHeader(int k) =>
            IsWord(k, "function", "procedure") && !IsSym(k - 1, ":") && !IsSym(k - 1, "=") && k + 1 < _sig.Count && _t[_sig[k + 1]].Kind == Kind.Word;

        /// <summary>The header of the one routine <paramref name="name"/>.</summary>
        private int Header(string name)
        {
            var headers = new List<int>();
            for (int k = 0; k + 1 < _sig.Count; k++)
                if (IsWord(k, "function", "procedure") && IsWord(k + 1, name)) headers.Add(k);
            Assert.True(headers.Count == 1, $"the code defines {name} {headers.Count} times, not once");
            return headers[0];
        }

        /// <summary>The begin of the body of the routine whose header is at <paramref name="h"/>.</summary>
        private int Begin(int h)
        {
            int begin = -1;
            for (int j = h + 1; j < _sig.Count && begin < 0; j++)
            {
                if (IsWord(j, "begin")) begin = j;
                else if (IsHeader(j)) break; // the next routine: no body
            }
            Assert.True(begin > h, $"{Text(h + 1)} has no begin in the code");
            return begin;
        }

        /// <summary>The header and the begin and end of the body of the one routine <paramref name="name"/>.</summary>
        private (int Header, int Begin, int End) Find(string name)
        {
            int h = Header(name), begin = Begin(h);
            return (h, begin, MatchingEnd(begin));
        }

        /// <summary>
        /// The last token of the routine whose header is at <paramref name="h"/>: the semicolon after the <c>end</c> of its
        /// own begin (that <c>end</c> when no semicolon follows), or, for a routine Windows provides, the semicolon after
        /// its <c>external</c> string. Fails on an <c>external</c> that is not <c>external '...';</c>.
        /// </summary>
        private int Last(int h)
        {
            int semicolon = h + 2;
            while (semicolon < _sig.Count && !IsSym(semicolon, ";")) semicolon = _close[semicolon] > semicolon ? _close[semicolon] + 1 : semicolon + 1;
            if (IsWord(semicolon + 1, "external"))
            {
                Assert.True(semicolon + 3 < _sig.Count && _t[_sig[semicolon + 2]].Kind == Kind.String && IsSym(semicolon + 3, ";"),
                    $"{Text(h + 1)} is not declared as external '...';");
                return semicolon + 3;
            }
            int end = MatchingEnd(Begin(h));
            return IsSym(end + 1, ";") ? end + 1 : end;
        }

        public string Routine(string name)
        {
            int h = Header(name);
            return _s[_t[_sig[h]].Start.._t[_sig[Last(h)]].End];
        }

        /// <summary>The name after each routine header of the code (<see cref="ScriptCode.PascalRoutineNames"/>), in order.</summary>
        public List<string> RoutineNames() => Enumerable.Range(0, _sig.Count).Where(IsHeader).Select(k => Text(k + 1)).ToList();

        /// <summary>The tokens of the code outside its routines (<see cref="ScriptCode.PascalOutsideRoutines"/>), in order.</summary>
        public List<string> OutsideRoutines()
        {
            var outside = new List<string>();
            for (int k = 0; k < _sig.Count; k++)
            {
                if (IsHeader(k)) k = Last(k);
                else outside.Add(Text(k));
            }
            return outside;
        }

        /// <summary>
        /// Reports a statement: the list it is a statement of (the place where that list starts; a case branch is a list of
        /// its own), its tokens [from, to), and the heads of the blocks it is in.
        /// </summary>
        private delegate void Report(int list, int from, int to, List<string> heads);

        /// <summary>Every statement of the routine's lists and case branches (<see cref="PascalStatements"/>), in the order they start.</summary>
        public List<(string Statement, IReadOnlyList<string> Heads)> All(string name)
        {
            var (_, begin, end) = Find(name);
            var all = new List<(int From, string Statement, IReadOnlyList<string> Heads)>();
            List(begin + 1, end, [], (_, from, to, heads) => all.Add((from, Code(from, to), heads.ToList())));
            return all.OrderBy(s => s.From).Select(s => (s.Statement, s.Heads)).ToList();
        }

        /// <summary>The statements of the code itself, read as a statement list: each as <see cref="Code"/> reads it.</summary>
        public List<string> Top()
        {
            var top = new List<string>();
            List(0, _sig.Count, [], (list, from, to, _) => { if (list == 0) top.Add(Code(from, to)); });
            return top;
        }

        /// <summary>For each place where the routine holds <paramref name="wanted"/> as consecutive statements of one list: the heads around them.</summary>
        public List<List<string>> Around(string name, IReadOnlyList<string> wanted)
        {
            var (_, begin, end) = Find(name);
            var lists = new Dictionary<int, List<(string Code, string Shape, List<string> Heads)>>();
            List(begin + 1, end, [], (list, from, to, heads) =>
            {
                if (!lists.TryGetValue(list, out var statements)) lists[list] = statements = [];
                statements.Add((Code(from, to), Shape(from, to), heads));
            });
            var found = new List<List<string>>();
            foreach (var statements in lists.Values)
                for (int i = 0; i + wanted.Count <= statements.Count; i++)
                    if (Enumerable.Range(0, wanted.Count).All(w => statements[i + w].Code == wanted[w] || statements[i + w].Shape == wanted[w])) found.Add([.. statements[i].Heads]);
            return found;
        }

        /// <summary>The statements of the list in [from, stop), each reported with the heads of the blocks it is in.</summary>
        private void List(int from, int stop, List<string> heads, Report statement)
        {
            for (int k = from; k < stop;)
            {
                if (IsSym(k, ";")) { k++; continue; }
                int end = Math.Min(Math.Max(Walk(k, k, heads, statement), k + 1), stop);
                statement(from, k, end, heads);
                k = end;
            }
        }

        private static List<string> With(List<string> heads, string head) => [.. heads, head];

        /// <summary>
        /// Reads the statement at <paramref name="k"/>, whose blocks' heads start at <paramref name="start"/> (the start of the
        /// statement in its list), and each list and case branch in it. The place after the statement.
        /// </summary>
        private int Walk(int k, int start, List<string> heads, Report statement)
        {
            if (k >= _sig.Count) return k;
            if (IsWord(k, "begin"))
            {
                int end = MatchingEnd(k);
                List(k + 1, end, With(heads, Head(start, k)), statement);
                return end + 1;
            }
            if (IsWord(k, "try"))
            {
                int end = MatchingEnd(k), middle = TryMiddle(k, end);
                string before = Head(start, k);
                List(k + 1, middle < 0 ? end : middle, With(heads, Join(before, "try")), statement);
                if (middle >= 0) List(middle + 1, end, With(heads, Join(before, "try ... " + Text(middle))), statement);
                return end + 1;
            }
            if (IsWord(k, "repeat"))
            {
                int until = Until(k), end = SimpleEnd(until + 1);
                List(k + 1, until, With(heads, Join(Head(start, k), "repeat ... " + Code(until, end))), statement);
                return end;
            }
            if (IsWord(k, "case"))
            {
                int end = MatchingEnd(k), of = Next(k + 1, "of");
                for (int j = of < 0 ? end : of + 1; j < end;)
                {
                    if (IsSym(j, ";")) { j++; continue; }
                    if (IsWord(j, "else"))
                    {
                        List(j + 1, end, With(heads, Join(Head(start, j), "else")), statement);
                        break;
                    }
                    int colon = j;
                    while (colon < end && !IsSym(colon, ":")) colon = _close[colon] > colon ? _close[colon] + 1 : colon + 1;
                    int next = Math.Min(Math.Max(Walk(colon + 1, start, heads, statement), colon + 1), end);
                    // The branch's own statement, unless it is a begin ... end (whose statements are a list of their own).
                    if (next > colon + 1 && !IsWord(colon + 1, "begin")) statement(colon + 1, colon + 1, next, With(heads, Head(start, colon + 1)));
                    j = next;
                }
                return end + 1;
            }
            if (IsWord(k, "if"))
            {
                int then = Next(k + 1, "then");
                if (then < 0) return SimpleEnd(k);
                int end = Walk(then + 1, start, heads, statement);
                return IsWord(end, "else") ? Walk(end + 1, start, heads, statement) : end;
            }
            if (IsWord(k, "while", "for", "with"))
            {
                int @do = Next(k + 1, "do");
                return @do < 0 ? SimpleEnd(k) : Walk(@do + 1, start, heads, statement);
            }
            return SimpleEnd(k);
        }

        private static string Join(string before, string word) => before.Length == 0 ? word : before + " " + word;

        /// <summary>
        /// The code from <paramref name="from"/> to <paramref name="to"/> (before it), each block that ends in it shown as
        /// <c>begin ... end</c> (a case statement that holds <paramref name="to"/> is kept: its selector and labels are its head).
        /// </summary>
        private string Head(int from, int to) => Elided(from, to, from);

        /// <summary>The statement from <paramref name="from"/> to <paramref name="to"/> (before it), each block in it shown as <c>begin ... end</c>.</summary>
        private string Shape(int from, int to) => Elided(from, to, from + 1);

        /// <summary>
        /// The code from <paramref name="from"/> to <paramref name="to"/> (before it), read as <see cref="Code"/> reads it, each
        /// block that starts at <paramref name="scan"/> or later and ends before <paramref name="to"/> shown as <c>begin ... end</c>
        /// (<c>try ... end</c>, <c>case ... end</c>, <c>repeat ... until</c>).
        /// </summary>
        private string Elided(int from, int to, int scan)
        {
            to = Math.Min(to, _sig.Count);
            if (from >= to) return "";
            var sb = new StringBuilder();
            int at = _sig[from];
            for (int j = scan; j < to; j++)
            {
                if (!IsWord(j, "begin", "try", "case", "repeat")) continue;
                int close = IsWord(j, "repeat") ? Until(j) : MatchingEnd(j);
                if (close >= to) continue;
                sb.Append(Spaced(at, _sig[j])).Append(IsWord(j, "repeat") ? "repeat ... until" : Text(j).ToLowerInvariant() + " ... end");
                at = _sig[close] + 1;
                j = close;
            }
            return sb.Append(Spaced(at, _sig[to - 1] + 1)).ToString().Trim();
        }

        /// <summary>
        /// The code of the tokens from <paramref name="from"/> to <paramref name="to"/> (before it): without comments, white
        /// space between tokens made one space, each string as written.
        /// </summary>
        private string Code(int from, int to)
        {
            to = Math.Min(to, _sig.Count);
            return from >= to ? "" : Spaced(_sig[from], _sig[to - 1] + 1);
        }

        /// <summary>
        /// The text of the tokens <paramref name="from"/> to <paramref name="to"/> (before it) of the whole list: each run of
        /// white space and comments one space (at either end too), every other token, a string included, as written.
        /// </summary>
        private string Spaced(int from, int to)
        {
            var sb = new StringBuilder();
            bool gap = false;
            for (int k = from; k < to; k++)
            {
                if (_t[k].Kind is Kind.Space or Kind.Newline or Kind.Comment) { gap = true; continue; }
                if (gap) sb.Append(' ');
                gap = false;
                sb.Append(_s, _t[k].Start, _t[k].End - _t[k].Start);
            }
            return gap ? sb.Append(' ').ToString() : sb.ToString();
        }

        /// <summary>The word <paramref name="word"/> from <paramref name="from"/>, before the statement ends; -1 if none.</summary>
        private int Next(int from, string word)
        {
            for (int j = from; j < _sig.Count; j++)
            {
                if (_close[j] > j) { j = _close[j]; continue; }
                if (IsWord(j, word)) return j;
                if (IsSym(j, ";") || IsWord(j, "begin", "end")) return -1;
            }
            return -1;
        }

        /// <summary>The end of a statement with no statement inside it: its semicolon, or the word that ends the list it is in.</summary>
        private int SimpleEnd(int k)
        {
            for (int j = k; j < _sig.Count; j++)
            {
                if (_close[j] > j) { j = _close[j]; continue; }
                if (IsSym(j, ";") || IsWord(j, "end", "else", "except", "finally", "until")) return j;
            }
            return _sig.Count;
        }

        /// <summary>The end that closes the begin, try or case at <paramref name="k"/>.</summary>
        private int MatchingEnd(int k)
        {
            int depth = 0;
            for (int j = k; j < _sig.Count; j++)
            {
                if (IsWord(j, "begin", "try", "case")) depth++;
                else if (IsWord(j, "end") && --depth == 0) return j;
            }
            return _sig.Count - 1;
        }

        /// <summary>The except or finally of the try at <paramref name="k"/>, which ends at <paramref name="close"/>; -1 if none.</summary>
        private int TryMiddle(int k, int close)
        {
            int depth = 0;
            for (int j = k + 1; j < close; j++)
            {
                if (IsWord(j, "begin", "try", "case")) depth++;
                else if (IsWord(j, "end")) depth--;
                else if (depth == 0 && IsWord(j, "except", "finally")) return j;
            }
            return -1;
        }

        /// <summary>The until that closes the repeat at <paramref name="k"/>.</summary>
        private int Until(int k)
        {
            int depth = 0;
            for (int j = k; j < _sig.Count; j++)
            {
                if (IsWord(j, "repeat")) depth++;
                else if (IsWord(j, "until") && --depth == 0) return j;
            }
            return _sig.Count - 1;
        }

        /// <summary>
        /// Pascal's tokens, its comments and strings read as Pascal Script reads them: a comment from <c>{</c> to the next
        /// <c>}</c>, from <c>(*</c> to the next <c>*)</c> that starts at its <c>*</c> or later (so <c>(*)</c> is a whole
        /// one), or from <c>//</c> to the line's end; a string from <c>'</c> to the next lone <c>'</c> (<c>''</c> is a quote
        /// in it) or the line's end; an unclosed comment runs to the end of the code. A line ends at a CR or an LF. White
        /// space is a run of spaces, tabs, form feeds and vertical tabs; words (with <c>#13</c>) and symbols are the rest.
        /// </summary>
        private static List<Token> Lex(string s)
        {
            var list = new List<Token>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                int start = i;
                char c = s[i];
                Kind kind;
                if (c is '\r' or '\n') { i += c == '\r' && At(s, i + 1, '\n') ? 2 : 1; kind = Kind.Newline; }
                else if (c is ' ' or '\t' or '\f' or '\v') { while (i < n && s[i] is ' ' or '\t' or '\f' or '\v') i++; kind = Kind.Space; }
                else if (c == '{')
                {
                    int e = s.IndexOf('}', i + 1);
                    i = e < 0 ? n : e + 1;
                    kind = Kind.Comment;
                }
                else if (c == '(' && At(s, i + 1, '*'))
                {
                    // Its end is looked for from its '*' on, as Pascal Script looks for it: "(*)" is a whole comment.
                    int e = s.IndexOf("*)", i + 1, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 2;
                    kind = Kind.Comment;
                }
                else if (c == '/' && At(s, i + 1, '/')) { while (i < n && s[i] is not ('\r' or '\n')) i++; kind = Kind.Comment; }
                else if (c == '\'')
                {
                    i++;
                    while (i < n && s[i] is not ('\r' or '\n'))
                    {
                        if (s[i] == '\'') { if (At(s, i + 1, '\'')) { i += 2; continue; } i++; break; }
                        i++;
                    }
                    kind = Kind.String;
                }
                else if (char.IsLetterOrDigit(c) || c is '_' or '#' or '$')
                {
                    i++;
                    while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    kind = Kind.Word;
                }
                else if (c is ':' or '<' or '>' && At(s, i + 1, '=') || c == '<' && At(s, i + 1, '>')) { i += 2; kind = Kind.Symbol; }
                else { i++; kind = Kind.Symbol; }
                list.Add(new Token(kind, start, i));
            }
            return list;
        }
    }

    // ------------------------------------------------------------------------------------------------ C#

    /// <summary>
    /// C# source without its comments and without what its strings and characters hold (each literal left empty, as
    /// <c>""</c> or <c>''</c>, an interpolated string's holes with it): so a pinned line of code is not met by a copy of it in
    /// a comment or a string. The code of every <c>#if</c>, from its line to its <c>#endif</c>, each branch of it, is left
    /// out too (its line ends kept): a build may leave it out, so a pinned line moved under one is not found. Other
    /// preprocessor lines are kept as written.
    /// </summary>
    public static string CSharp(string source)
    {
        var tokens = CsLex(source);
        var conditional = Conditional(source, tokens);
        var sb = new StringBuilder();
        for (int k = 0; k < tokens.Count; k++)
        {
            var token = tokens[k];
            string text = source[token.Start..token.End];
            if (conditional[k]) sb.Append(token.Kind == Kind.Newline ? text : "");
            else if (token.Kind == Kind.Comment) sb.Append(text.StartsWith("/*", StringComparison.Ordinal) ? " " : "");
            else if (token.Kind == Kind.String) sb.Append(text[0] == '\'' ? "''" : "\"\"");
            else sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The one member of the C# source that has <paramref name="header"/> at the start of a line (after its indentation) in
    /// its code, not in a comment, a string or the code of an <c>#if</c> (<see cref="CSharp"/>): fails unless there is
    /// exactly one, and when an <c>#if</c> starts inside it. As written (its comments too), from the header to the brace
    /// that closes its body.
    /// </summary>
    public static Found CSharpMember(string source, string header)
    {
        var tokens = CsLex(source);
        var conditional = Conditional(source, tokens);
        var words = Enumerable.Range(0, tokens.Count).Where(k => tokens[k].Kind == Kind.Word && !conditional[k]).Select(k => tokens[k].Start).ToHashSet();
        var at = Regex.Matches(source, $@"(?m)^[ \t]*{Regex.Escape(header)}").Select(m => m.Index + m.Value.Length - header.Length).Where(words.Contains).ToList();
        Assert.True(at.Count == 1, $"the source has {at.Count} members that start \"{header}\" in its code, not one");
        int depth = 0;
        foreach (var token in tokens.Where(t => t.Start >= at[0] && t.Kind == Kind.Symbol))
        {
            char c = source[token.Start];
            if (c == '{') { depth++; continue; }
            if (c != '}' || --depth != 0) continue;
            bool underIf = Enumerable.Range(0, tokens.Count).Any(k => conditional[k] && tokens[k].Start > at[0] && tokens[k].Start < token.End);
            Assert.False(underIf, $"the member that starts \"{header}\" has an #if in it");
            return new Found(source[at[0]..token.End]);
        }
        Assert.Fail($"the member that starts \"{header}\" has no closing brace");
        return default;
    }

    /// <summary>
    /// The tokens of the C# source's code, in order, each as written: none of its white space, comments or preprocessor
    /// lines, and nothing of the code of an <c>#if</c>, each branch of it (<see cref="CSharp"/>). Each string or character
    /// literal is one token, whole (a raw string with its quotes, an interpolated one with its holes); a word is one token,
    /// and every other character a token of its own.
    /// </summary>
    public static IReadOnlyList<string> CSharpTokens(string source)
    {
        var tokens = CsLex(source);
        var conditional = Conditional(source, tokens);
        return Enumerable.Range(0, tokens.Count)
            .Where(k => !conditional[k] && tokens[k].Kind is not (Kind.Space or Kind.Newline or Kind.Comment or Kind.Directive))
            .Select(k => source[tokens[k].Start..tokens[k].End]).ToList();
    }

    /// <summary>
    /// For each of the <paramref name="tokens"/> of the C# <paramref name="source"/>, whether it is in an <c>#if</c>: from
    /// its line to the line of its <c>#endif</c>, both included, each branch (<c>#elif</c>, <c>#else</c>) too.
    /// </summary>
    private static bool[] Conditional(string source, List<Token> tokens)
    {
        var conditional = new bool[tokens.Count];
        int depth = 0;
        for (int k = 0; k < tokens.Count; k++)
        {
            string directive = tokens[k].Kind == Kind.Directive ? Regex.Match(source[tokens[k].Start..tokens[k].End], @"^#\s*(\w+)").Groups[1].Value : "";
            if (directive == "if") depth++;
            conditional[k] = depth > 0;
            if (directive == "endif" && depth > 0) depth--;
        }
        return conditional;
    }

    /// <summary>C#'s tokens: comments, preprocessor lines, strings (regular, verbatim, interpolated with their holes, raw), characters, words and symbols.</summary>
    private static List<Token> CsLex(string s, List<Token>? holeComments = null)
    {
        var list = new List<Token>();
        int i = 0;
        CsLex(s, ref i, list, untilBrace: false, holeComments);
        return list;
    }

    /// <param name="holeComments">Given, the comments in an interpolated string's holes go there (they are no tokens of
    /// their own: the string is one).</param>
    private static void CsLex(string s, ref int i, List<Token>? list, bool untilBrace, List<Token>? holeComments = null)
    {
        int depth = 0, n = s.Length;
        while (i < n)
        {
            int start = i;
            char c = s[i];
            Kind kind;
            if (c is '\r' or '\n') { i += c == '\r' && At(s, i + 1, '\n') ? 2 : 1; kind = Kind.Newline; }
            else if (c is ' ' or '\t' or '\f' or '\v') { while (i < n && s[i] is ' ' or '\t' or '\f' or '\v') i++; kind = Kind.Space; }
            else if (c == '#' && !untilBrace && LineStartBefore(s, i)) { while (i < n && s[i] is not ('\r' or '\n')) i++; kind = Kind.Directive; }
            else if (c == '/' && At(s, i + 1, '/')) { while (i < n && s[i] is not ('\r' or '\n')) i++; kind = Kind.Comment; }
            else if (c == '/' && At(s, i + 1, '*')) { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2; kind = Kind.Comment; }
            else if (c == '"' || c is '$' or '@' && StringAhead(s, i)) { i = CsString(s, i, holeComments); kind = Kind.String; }
            else if (c == '\'')
            {
                i++;
                if (At(s, i, '\\')) i += 2;
                while (i < n && s[i] != '\'' && s[i] is not ('\r' or '\n')) i++;
                if (i < n && s[i] == '\'') i++;
                kind = Kind.String;
            }
            else if (char.IsLetterOrDigit(c) || c == '_') { while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; kind = Kind.Word; }
            else
            {
                i++;
                kind = Kind.Symbol;
                if (untilBrace && c == '{') depth++;
                else if (untilBrace && c == '}' && depth-- == 0) { list?.Add(new Token(kind, start, i)); return; }
            }
            if (list is null && kind == Kind.Comment) holeComments?.Add(new Token(kind, start, i));
            list?.Add(new Token(kind, start, i));
        }

        static bool StringAhead(string s, int i)
        {
            while (i < s.Length && s[i] is '$' or '@') i++;
            return At(s, i, '"');
        }
    }

    private static int CsString(string s, int i, List<Token>? holeComments = null)
    {
        int n = s.Length, dollars = 0;
        bool verbatim = false;
        while (i < n && s[i] is '$' or '@') { if (s[i] == '$') dollars++; else verbatim = true; i++; }
        int quotes = 0;
        while (i + quotes < n && s[i + quotes] == '"') quotes++;
        if (!verbatim && quotes >= 3)
        {
            // A raw string ends at its first run of as many quotes as it opened with, outside a hole. With n dollars a run
            // of n braces or more opens a hole (the braces before its last n are text), which ends at its first closing
            // brace, n of them in all.
            string close = new('"', quotes);
            int j = i + quotes;
            while (j < n)
            {
                if (s.AsSpan(j).StartsWith(close, StringComparison.Ordinal)) return j + quotes;
                if (dollars > 0 && s[j] == '{')
                {
                    int run = 0;
                    while (j + run < n && s[j + run] == '{') run++;
                    j += run;
                    if (run < dollars) continue;
                    CsLex(s, ref j, null, untilBrace: true, holeComments);
                    j += dollars - 1;
                    continue;
                }
                j++;
            }
            return n;
        }
        i++;
        while (i < n)
        {
            char c = s[i];
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == '"') { if (verbatim && At(s, i + 1, '"')) { i += 2; continue; } return i + 1; }
            if (dollars > 0 && c == '{') { if (At(s, i + 1, '{')) { i += 2; continue; } i++; CsLex(s, ref i, null, untilBrace: true, holeComments); continue; }
            if (dollars > 0 && c == '}' && At(s, i + 1, '}')) { i += 2; continue; }
            if (!verbatim && c is '\r' or '\n') return i;
            i++;
        }
        return n;
    }

    private static bool LineStartBefore(string s, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (s[j] is '\r' or '\n') return true;
            if (s[j] is not (' ' or '\t')) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ comments

    /// <summary>
    /// Where each comment of C# source is (its start and end, its opening included), in order, read by the tokens
    /// <see cref="CSharp"/> reads: a "//" in a string or a character is not one, while one in an interpolated string's
    /// hole is, and so is the rest of a preprocessor line from the first "//" in it (where that is the directive's text,
    /// as after #region, #endregion, #error and #warning, it is read as a comment all the same). JSON with comments reads
    /// the same way.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> CSharpComments(string source)
    {
        var inHoles = new List<Token>();
        var spans = new List<(int Start, int End)>();
        foreach (var token in CsLex(source, inHoles))
        {
            if (token.Kind == Kind.Comment) spans.Add((token.Start, token.End));
            else if (token.Kind == Kind.Directive && source.IndexOf("//", token.Start, token.End - token.Start, StringComparison.Ordinal) is var at and >= 0)
                spans.Add((at, token.End));
        }
        spans.AddRange(inHoles.Select(t => (t.Start, t.End)));
        return spans.OrderBy(s => s.Start).ToList();
    }

    /// <summary>Where each comment of a PowerShell script is (its start and end, its opening included), read by the tokens <see cref="PowerShell"/> reads.</summary>
    public static IReadOnlyList<(int Start, int End)> PowerShellComments(string script) => new PsCode(script).CommentSpans();

    /// <summary>Where each comment of Pascal code is (its start and end, its opening included), read by the tokens <see cref="Pascal"/> reads.</summary>
    public static IReadOnlyList<(int Start, int End)> PascalComments(string code) => new PascalCode(code).CommentSpans();

    // ------------------------------------------------------------------------------------------------ shared

    private static bool At(string s, int i, char c) => i < s.Length && s[i] == c;

    /// <summary>For each opening bracket among <paramref name="count"/> tokens, the place of its closing one (else -1).</summary>
    private static int[] Brackets(int count, Func<int, string?> symbol)
    {
        var close = Enumerable.Repeat(-1, count).ToArray();
        var open = new Stack<(int At, char Bracket)>();
        for (int k = 0; k < count; k++)
        {
            switch (symbol(k))
            {
                case "(": open.Push((k, ')')); break;
                case "{": open.Push((k, '}')); break;
                case "[": open.Push((k, ']')); break;
                case ")" or "}" or "]" when open.Count > 0 && open.Peek().Bracket == symbol(k)![0]:
                    close[open.Pop().At] = k;
                    break;
            }
        }
        return close;
    }
}
