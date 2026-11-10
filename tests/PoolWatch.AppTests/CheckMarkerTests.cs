using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The CI scripts' check markers, the layer over the text pins (<see cref="ScriptCode"/>). A pin holds a check's line
/// as written; it cannot tell whether the check runs. So each check of a CI script that a test pins prints a fixed line,
/// <c>CHECK-RAN: &lt;id&gt;</c>, in its own block as the statement right after it or right after the last check of the
/// run of checks it is in, or as the statement right after the try statement (with a finally and no catch) whose body
/// holds it (each test that pins such a check holds that too: <see cref="ScriptCode.CheckThenMarker"/>,
/// <see cref="ScriptCode.OnlyCheckThenMarker"/>, <see cref="UninstallerTests.ChecksWithoutAMarker"/>);
/// <c>ci/expected-checks.txt</c> lists each as <c>&lt;script&gt; &lt;id&gt;</c>; and in the workflow files
/// (.github/workflows), a step that runs a listed script as <c>./ci/&lt;script&gt;</c> saves its output, and the
/// step right after it fails the job, naming each check, when a marker the list holds for that script is not in
/// that output, or when the output lacks the line Complete-Assertions prints once no check failed. The soak runs its
/// pinned checks only on the ui leg of its job's matrix, so its verification runs on that leg, and on its other leg
/// the next step looks for that line alone. These tests hold the scripts, the list and the workflows to each other
/// as text: each step with a line other than a comment line that names a listed script (its file name, in any case)
/// is that script's verification (or, for the soak, the check of its other legs), or names it only in lines that run
/// it as <c>./ci/&lt;script&gt;</c> with its output saved, and such a line outside the steps and jobs they read fails
/// them. They also hold that each job with such a line has no condition and needs no job that has one (but the soak's
/// own: it runs on demand and on a release tag), that nothing in it goes on after a failure, and that its matrix has
/// the leg its verification runs on; and that the assertion helpers every check goes through record each failed check
/// and fail the run on any, that no other statement of ci/ names their list of failures, ErrorActionPreference or the
/// words of the line Complete-Assertions prints once no check failed, that each listed script names Complete-Assertions
/// only in its last statement, and that common.ps1, which each of them dot-sources, names it only where it defines
/// it. So in a run of such a job, a pinned check that did not run (for the soak, on its ui leg), or a run that ended
/// before Complete-Assertions said that no check failed (on any leg), fails the job. The verification itself runs only
/// in CI. A marker shows that the check's statement ran, not what it read: neither layer checks the data a check reads.
/// </summary>
public class CheckMarkerTests
{
    private const string ListFile = "ci/expected-checks.txt";

    /// <summary>
    /// The listed scripts whose verification runs only on some legs of their job's matrix, with that condition: the soak's
    /// pinned checks are its -Ui ones, which only its ui leg runs.
    /// </summary>
    private static readonly Dictionary<string, string> Conditions = new(StringComparer.Ordinal) { ["soak.ps1"] = "matrix.mode == 'ui'" };

    /// <summary>
    /// The listed scripts whose job runs only on some runs of the workflow, with that condition as the job's <c>if:</c>: the
    /// soak runs on demand and on a release tag (the release's sign job needs it).
    /// </summary>
    private static readonly Dictionary<string, string> JobConditions = new(StringComparer.Ordinal)
    {
        ["soak.ps1"] = "github.event_name == 'workflow_dispatch' || startsWith(github.ref, 'refs/tags/v')",
        // The agentic-load overhead check runs beside it, on the same runs, and the release needs it too.
        ["agentic-soak.ps1"] = "github.event_name == 'workflow_dispatch' || startsWith(github.ref, 'refs/tags/v')",
    };

    /// <summary>
    /// The verification's own lines (its run), the same for every listed script (<see cref="Verification"/>): it reads the
    /// output the script's step saved, one trimmed line each; takes the ids the list holds for the script; fails when the
    /// list holds none for it; collects each id whose <c>CHECK-RAN: &lt;id&gt;</c> is not a line of the output; fails,
    /// naming them, when there is any; and fails when no line of the output is the one Complete-Assertions prints when no
    /// check failed (<c>&lt;name&gt; : all assertions passed</c>), as when the script ended before it.
    /// </summary>
    internal static readonly string[] VerificationRun =
    [
        @"$ran = @(Get-Content -LiteralPath ""$env:RUNNER_TEMP/$env:CI_SCRIPT.out"" | ForEach-Object { ""$_"".Trim() })",
        @"$expected = @(Get-Content -LiteralPath ci/expected-checks.txt | ForEach-Object { if ($_ -match ""^$([regex]::Escape($env:CI_SCRIPT)) (\S+)\s*$"") { $Matches[1] } })",
        @"if ($expected.Count -eq 0) { throw ""ci/expected-checks.txt lists no check for $env:CI_SCRIPT"" }",
        @"$missing = @($expected | Where-Object { ""CHECK-RAN: $_"" -notin $ran })",
        @"if ($missing.Count -gt 0) { throw ""$env:CI_SCRIPT did not run these checks: $($missing -join ', ')"" }",
        @"if (@($ran -match ' : all assertions passed$').Count -eq 0) { throw ""$env:CI_SCRIPT ended before Complete-Assertions said that no check failed"" }",
        @"Write-Host ""$env:CI_SCRIPT ran each of the $($expected.Count) checks that ci/expected-checks.txt lists for it""",
    ];

    /// <summary>
    /// The run of the step that, on the legs of a script's matrix where its verification does not run
    /// (<see cref="Conditions"/>), looks only for the line Complete-Assertions prints when no check failed: the
    /// verification's first line and its last check (<see cref="VerificationRun"/>), then a line that says so.
    /// </summary>
    internal static readonly string[] PassedLineRun =
    [
        VerificationRun[0],
        VerificationRun[5],
        @"Write-Host ""$env:CI_SCRIPT said that no check failed""",
    ];

    private static readonly Regex MarkerLine = new(@"(?m)^[ \t]*Write-Host 'CHECK-RAN: ([a-z0-9]+(?:-[a-z0-9]+)*)'[ \t]*\r?$");

    private static string Repo(params string[] path) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path]));

    private static string OneLine(string text) => UninstallerTests.OneLine(text);

    /// <summary>Every script of the ci folder, by its file name.</summary>
    private static Dictionary<string, string> Scripts() =>
        Directory.GetFiles(Path.Combine(TrayIconsTests.RepoRoot(), "ci"), "*.ps1").ToDictionary(f => Path.GetFileName(f), File.ReadAllText, StringComparer.Ordinal);

    private static string List() => Repo("ci", "expected-checks.txt");

    /// <summary>Every workflow file of .github/workflows (.yml or .yaml), by its file name.</summary>
    private static Dictionary<string, string> Workflows() =>
        Directory.GetFiles(Path.Combine(TrayIconsTests.RepoRoot(), ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText, StringComparer.Ordinal);

    /// <summary><paramref name="text"/> with <paramref name="find"/>, which must be there once, replaced.</summary>
    private static string Once(string text, string find, string replace)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(find, at + 1, StringComparison.Ordinal) < 0, $"\"{find}\" is not in the text once");
        return text[..at] + replace + text[(at + find.Length)..];
    }

    // ---------------------------------------------------------------- the scripts and the list

    /// <summary>
    /// The ids of the markers a script prints, read in its code (comments out, <see cref="ScriptCode.PowerShell"/>): each
    /// <see cref="ScriptCode.Marker"/> statement on a line of its own; and why not, when the code names CHECK-RAN anywhere
    /// else (a marker written another way, whose text the list and the verification would not match).
    /// </summary>
    internal static (List<string> Ids, string? Wrong) MarkersOf(string name, string script)
    {
        string code = ScriptCode.PowerShell(script);
        var ids = MarkerLine.Matches(code).Select(m => m.Groups[1].Value).ToList();
        int named = Regex.Matches(code, "CHECK-RAN").Count;
        return (ids, named == ids.Count ? null : $"{name} names CHECK-RAN {named - ids.Count} times other than in a line {ScriptCode.Marker("<id>")} of its own");
    }

    /// <summary>The entries of the list, as (script, id), and why not when a line is neither one nor a comment or blank.</summary>
    internal static (List<(string Script, string Id)> Entries, string? Wrong) Entries(string list)
    {
        var entries = new List<(string, string)>();
        foreach (string raw in list.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var entry = Regex.Match(line, @"^([\w.-]+\.ps1) ([a-z0-9]+(?:-[a-z0-9]+)*)$");
            if (!entry.Success) return (entries, $"{ListFile} has a line that is not \"<script> <check-id>\": {line}");
            entries.Add((entry.Groups[1].Value, entry.Groups[2].Value));
        }
        return (entries, null);
    }

    /// <summary>
    /// Why the markers the <paramref name="scripts"/> print and the <paramref name="list"/> differ, or null when every
    /// marker a script prints is listed for it and every listed marker is printed by its script exactly once (each id once
    /// in the list too).
    /// </summary>
    internal static string? WhyTheMarkersAndTheListDiffer(IReadOnlyDictionary<string, string> scripts, string list)
    {
        var (listed, malformed) = Entries(list);
        if (malformed is not null) return malformed;
        if (listed.Count == 0) return $"{ListFile} lists no check";
        if (listed.FirstOrDefault(e => !scripts.ContainsKey(e.Script)) is { Script: not null } stray) return $"{ListFile} lists {stray.Script}, which is not in ci/";
        if (listed.GroupBy(e => e.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"{ListFile} lists {twice.Key} {twice.Count()} times, not once";
        var printed = new List<(string Script, string Id)>();
        foreach (var (name, script) in scripts.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var (ids, wrong) = MarkersOf(name, script);
            if (wrong is not null) return wrong;
            if (ids.GroupBy(i => i).FirstOrDefault(g => g.Count() > 1) is { } again) return $"{name} prints the marker {again.Key} {again.Count()} times, not once";
            printed.AddRange(ids.Select(id => (name, id)));
        }
        if (listed.FirstOrDefault(e => !printed.Contains(e)) is { Script: not null } unprinted) return $"{ListFile} lists {unprinted.Id} for {unprinted.Script}, which does not print it";
        if (printed.FirstOrDefault(p => !listed.Contains(p)) is { Script: not null } unlisted) return $"{unlisted.Script} prints the marker {unlisted.Id}, which {ListFile} does not list for it";
        return null;
    }

    [Fact]
    public void EveryMarkerAScriptPrintsIsListedAndEveryListedMarkerIsPrintedOnce()
    {
        Assert.Null(WhyTheMarkersAndTheListDiffer(Scripts(), List()));
        var (entries, _) = Entries(List());
        Assert.Equal(["agentic-soak.ps1", "installer-e2e.ps1", "m4-ui.ps1", "soak.ps1"], entries.Select(e => e.Script).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each of these differences between the scripts and the list fails the test above: a marker taken out of its script or
    /// commented out (the list holds a check that no longer says it ran), a list entry with no marker, an entry for the
    /// wrong script, a marker the list does not hold, a marker written twice or written another way, a line that is not an
    /// entry, an id listed twice.
    /// </summary>
    [Theory]
    [InlineData("a marker taken out", "lists ui-three-tabs for m4-ui.ps1, which does not print it")]
    [InlineData("a marker commented out", "lists ui-three-tabs for m4-ui.ps1, which does not print it")]
    [InlineData("a list entry with no marker", "lists ui-no-such-check for m4-ui.ps1, which does not print it")]
    [InlineData("an entry for another script", "lists ui-three-tabs for soak.ps1, which does not print it")]
    [InlineData("a marker the list does not hold", "prints the marker ui-new-check, which ci/expected-checks.txt does not list for it")]
    [InlineData("a marker written twice", "prints the marker ui-three-tabs 2 times")]
    [InlineData("a marker written another way", "names CHECK-RAN 1 times other than in a line")]
    [InlineData("a line that is not an entry", "has a line that is not")]
    [InlineData("an id listed twice", "lists ui-three-tabs 2 times")]
    [InlineData("a script that is not there", "lists gone.ps1, which is not in ci/")]
    public void TheMarkersAndTheListFailOnEachDifference(string change, string reason)
    {
        var scripts = Scripts();
        string list = List();
        const string marker = "    Write-Host 'CHECK-RAN: ui-three-tabs'";
        string ui = scripts["m4-ui.ps1"];
        switch (change)
        {
            case "a marker taken out": scripts["m4-ui.ps1"] = Once(ui, marker, ""); break;
            case "a marker commented out": scripts["m4-ui.ps1"] = Once(ui, marker, "    # " + marker.Trim()); break;
            case "a list entry with no marker": list += "m4-ui.ps1 ui-no-such-check\r\n"; break;
            case "an entry for another script": list = Once(list, "m4-ui.ps1 ui-three-tabs", "soak.ps1 ui-three-tabs"); break;
            case "a marker the list does not hold": scripts["m4-ui.ps1"] = Once(ui, marker, marker + "\r\n    Write-Host 'CHECK-RAN: ui-new-check'"); break;
            case "a marker written twice": scripts["m4-ui.ps1"] = Once(ui, marker, marker + "\r\n" + marker); break;
            case "a marker written another way": scripts["m4-ui.ps1"] = Once(ui, marker, "    Write-Host \"CHECK-RAN: $('ui-three-tabs')\""); break;
            case "a line that is not an entry": list = Once(list, "m4-ui.ps1 ui-three-tabs", "m4-ui.ps1  ui-three-tabs"); break;
            case "an id listed twice": list += "m4-ui.ps1 ui-three-tabs\r\n"; break;
            case "a script that is not there": list += "gone.ps1 gone-check\r\n"; break;
            default: Assert.Fail("no such change: " + change); break;
        }
        Assert.Null(WhyTheMarkersAndTheListDiffer(Scripts(), List()));
        string? why = WhyTheMarkersAndTheListDiffer(scripts, list);
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    // ---------------------------------------------------------------- the helpers every check goes through

    [Fact]
    public void TheAssertionHelpersRecordEachFailedCheckAndFailTheRunOnAny() => HoldTheAssertionHelpers(Scripts());

    /// <summary>
    /// Each of these changes to a copy of the CI scripts fails the test above, and none is in a statement that test holds
    /// to its text: the list of failures emptied by another statement; <c>$ErrorActionPreference</c> set by another
    /// script, by an assignment or by Set-Variable; the soak's Complete-Assertions called early on its headless leg, before
    /// its -Assert checks; the passed line written by another statement; and a call of Complete-Assertions in common.ps1.
    /// </summary>
    [Theory]
    [InlineData("m4-ui.ps1", "$script:Failures.Clear()", "m4-ui.ps1 names the list of failures 1 times")]
    [InlineData("m4-ui.ps1", "$ErrorActionPreference = 'Continue'", "m4-ui.ps1 names ErrorActionPreference 1 times")]
    [InlineData("m4-ui.ps1", "Set-Variable -Name ErrorActionPreference -Value Continue", "m4-ui.ps1 names ErrorActionPreference 1 times")]
    [InlineData("soak.ps1", "if (-not $Ui) { Complete-Assertions 'Soak' }", "soak.ps1 names Complete-Assertions 2 times")]
    [InlineData("soak.ps1", "if (-not $Ui) { Write-Host 'Soak : all assertions passed'; exit 0 }", "soak.ps1 has the words of the passed line 1 times")]
    [InlineData("common.ps1", "if ($Minutes -and -not $Ui) { Complete-Assertions 'Soak' }", "common.ps1 names Complete-Assertions 2 times")]
    public void TheAssertionHelpersTestFailsOnEachChange(string script, string added, string reason)
    {
        var scripts = Scripts();
        HoldTheAssertionHelpers(scripts);
        // The soak's change goes before its -Assert block, the UI test's before its last two statements, and common.ps1's
        // after its last line.
        if (script == "common.ps1") scripts[script] += "\r\n" + added + "\r\n";
        else
        {
            string before = script == "soak.ps1" ? "if ($Assert) {" : "Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue";
            scripts[script] = Once(scripts[script], before, added + "\r\n" + before);
        }
        string? why = ScriptCodeTests.WhyThePinFails(() => { HoldTheAssertionHelpers(scripts); return true; });
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    /// <summary>
    /// A marker prints after its check whatever the check found, so a failed check fails CI only through the two helpers
    /// every check goes through (ci/common.ps1). This holds them to their text, without comments and with every run of white
    /// space made one space: Assert-True records each failed check, and Complete-Assertions fails the script (exit 1) when
    /// any was recorded and otherwise prints its "all assertions passed" line, which the verification requires. It also
    /// holds what they depend on, by plain counts of names in the code of ci/ (comments out, strings in):
    /// <c>$ErrorActionPreference = 'Stop'</c>, set by a statement of common.ps1's top level, and ErrorActionPreference, in
    /// any case, named by no other statement of ci/; the list of failures, made by a statement of common.ps1's top level and
    /// named, in any case, by no statement of ci/ but that one and the two helpers; the words of the passed line ("all
    /// assertions passed"), in any case, in no code of ci/ but Complete-Assertions; neither helper defined by any other
    /// script of ci/; and each listed script dot-sourcing common.ps1 by a statement of its top level, and naming
    /// Complete-Assertions once in its code, in the last statement of its top level. A statement that reaches one of these
    /// without naming it as written (by a name built as the script runs, say) is not counted.
    /// </summary>
    private static void HoldTheAssertionHelpers(IReadOnlyDictionary<string, string> scripts)
    {
        string common = scripts["common.ps1"];
        string assertTrue = ScriptCode.PowerShellFunction(common, "Assert-True").Value, complete = ScriptCode.PowerShellFunction(common, "Complete-Assertions").Value;
        Assert.Equal(OneLine("""
            function Assert-True([bool]$cond, [string]$message) {
                if ($cond) { Write-Host "  [PASS] $message" }
                else { Write-Host "  [FAIL] $message"; $script:Failures.Add($message) }
            }
            """), OneLine(assertTrue));
        Assert.Equal(OneLine("""
            function Complete-Assertions([string]$name) {
                if ($script:Failures.Count -gt 0) {
                    Write-Host "$name : $($script:Failures.Count) assertion(s) failed:"
                    $script:Failures | ForEach-Object { Write-Host "   - $_" }
                    exit 1
                }
                Write-Host "$name : all assertions passed"
                exit 0
            }
            """), OneLine(complete));
        Assert.Equal("Stop", ScriptCode.PowerShellVariable(common, "ErrorActionPreference"));
        const string failures = "$script:Failures = New-Object System.Collections.Generic.List[string]";
        Assert.Contains(failures, ScriptCode.Statements(ScriptCode.PowerShell(common)).Select(OneLine));
        static int Named(string code) => Regex.Matches(code, "Failures", RegexOptions.IgnoreCase).Count;
        static int PassedWords(string code) => Regex.Matches(code, @"all\s+assertions\s+passed", RegexOptions.IgnoreCase).Count;
        Assert.Equal(1, PassedWords(complete));
        foreach (var (name, script) in scripts)
        {
            string code = ScriptCode.PowerShell(script);
            int preference = name == "common.ps1" ? 1 : 0, preferences = Regex.Matches(code, "ErrorActionPreference", RegexOptions.IgnoreCase).Count;
            Assert.True(preferences == preference, $"{name} names ErrorActionPreference {preferences} times in its code, not {preference} (in common.ps1's statement that sets it)");
            int named = name == "common.ps1" ? Named(failures) + Named(assertTrue) + Named(complete) : 0;
            Assert.True(Named(code) == named, $"{name} names the list of failures {Named(code)} times in its code, not {named} (in the statements this test holds)");
            int passed = name == "common.ps1" ? PassedWords(complete) : 0;
            Assert.True(PassedWords(code) == passed, $"{name} has the words of the passed line {PassedWords(code)} times in its code, not {passed} (in Complete-Assertions)");
            if (name == "common.ps1")
            {
                // Its code names Complete-Assertions only where it defines it: a call there would run in every script that
                // dot-sources it, before that script's own checks.
                int defined = Regex.Matches(code, "Complete-Assertions", RegexOptions.IgnoreCase).Count;
                Assert.True(defined == 1, $"common.ps1 names Complete-Assertions {defined} times in its code, not once (in its definition)");
                continue;
            }
            foreach (string helper in new[] { "Assert-True", "Complete-Assertions" })
                Assert.True(ScriptCode.PowerShellDefinitions(script, helper) == 0, $"{name} defines {helper} again");
        }
        foreach (string listed in Listed())
        {
            string code = ScriptCode.PowerShell(scripts[listed]);
            var top = ScriptCode.Statements(code).Select(OneLine).ToList();
            Assert.True(top.Contains(". \"$PSScriptRoot\\common.ps1\""), $"{listed} does not dot-source common.ps1 by a statement of its top level");
            Assert.Matches(@"^Complete-Assertions '[^']+'$", top[^1]);
            int completes = Regex.Matches(code, "Complete-Assertions", RegexOptions.IgnoreCase).Count;
            Assert.True(completes == 1, $"{listed} names Complete-Assertions {completes} times in its code, not once (in its last statement)");
        }
    }

    // ---------------------------------------------------------------- the workflows

    /// <summary>A job's header line: "  &lt;name&gt;:", maybe with a comment after it.</summary>
    private static readonly Regex JobHeader = new(@"^  ([\w-]+):\s*(#.*)?$");

    /// <summary>The workflow's lines, without their line ends.</summary>
    private static string[] LinesOf(string workflow) => workflow.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    /// <summary>
    /// The workflow's steps in order, each with its job and its lines (comment lines and trailing blank lines left out): a
    /// step starts at a line that starts "      - " and runs on while the lines are indented further, or blank.
    /// </summary>
    private static List<(string Job, List<string> Lines)> Steps(string workflow) => Steps(workflow, out _);

    /// <summary>
    /// <see cref="Steps(string)"/>, and the numbers (from 0) of the workflow's lines that are in them: a line that names a
    /// listed script and is not one of them is in a step this reader does not see (one indented otherwise, say).
    /// </summary>
    private static List<(string Job, List<string> Lines)> Steps(string workflow, out HashSet<int> read)
    {
        var steps = new List<(string Job, List<string> Lines)>();
        read = [];
        string job = "";
        List<string>? step = null;
        var lines = LinesOf(workflow);
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            if (IsComment(line)) continue;
            var header = JobHeader.Match(line);
            if (header.Success) { job = header.Groups[1].Value; step = null; continue; }
            if (line.StartsWith("      - ", StringComparison.Ordinal)) { step = [line]; steps.Add((job, step)); read.Add(n); continue; }
            if (line.Trim().Length == 0) { step?.Add(""); continue; }
            if (!line.StartsWith("        ", StringComparison.Ordinal)) { step = null; continue; }
            if (step is null) continue;
            step.Add(line);
            read.Add(n);
        }
        foreach (var (_, steplines) in steps) while (steplines[^1].Length == 0) steplines.RemoveAt(steplines.Count - 1);
        return steps;
    }

    /// <summary>
    /// The workflow's jobs, by name, each with its lines (comment lines left out): from its header, a line "  &lt;name&gt;:"
    /// under <c>jobs:</c>, to the next job's header or the next line that is not indented.
    /// </summary>
    private static Dictionary<string, List<string>> Jobs(string workflow) => Jobs(workflow, out _);

    /// <summary><see cref="Jobs(string)"/>, and the numbers (from 0) of the workflow's lines that are in them.</summary>
    private static Dictionary<string, List<string>> Jobs(string workflow, out HashSet<int> read)
    {
        var jobs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        read = [];
        List<string>? job = null;
        bool inJobs = false;
        var lines = LinesOf(workflow);
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            if (IsComment(line)) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { inJobs = line.StartsWith("jobs:", StringComparison.Ordinal); job = null; continue; }
            if (!inJobs) continue;
            var header = JobHeader.Match(line);
            if (header.Success) { jobs[header.Groups[1].Value] = job = []; read.Add(n); continue; }
            if (job is null) continue;
            job.Add(line);
            read.Add(n);
        }
        return jobs;
    }

    /// <summary>
    /// Why a line of a workflow names one of the <paramref name="listed"/> scripts outside the lines a reader read
    /// (<paramref name="read"/>, <paramref name="where"/> naming them), or null when none does. Comment lines are not read.
    /// </summary>
    private static string? NamedOutside(string file, string workflow, HashSet<int> read, IReadOnlyCollection<string> listed, string where)
    {
        var lines = LinesOf(workflow);
        for (int n = 0; n < lines.Length; n++)
        {
            if (read.Contains(n) || IsComment(lines[n])) continue;
            if (listed.FirstOrDefault(s => Names(s).IsMatch(lines[n])) is { } script)
                return $"line {n + 1} of {file} names {script} outside {where}: {lines[n].Trim()}";
        }
        return null;
    }

    /// <summary>The value of a key of the job itself (a line "    &lt;key&gt;: ..."), or null when the job has none.</summary>
    private static string? JobKey(List<string> job, string key) =>
        job.Select(l => Regex.Match(l, $@"^    {Regex.Escape(key)}:(.*)$")).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();

    /// <summary>The jobs a job needs: <c>needs: a</c>, <c>needs: [a, b]</c>, or a list of lines "      - a" under <c>needs:</c>.</summary>
    private static List<string> Needs(List<string> job)
    {
        int at = job.FindIndex(l => Regex.IsMatch(l, @"^    needs:"));
        if (at < 0) return [];
        string value = job[at]["    needs:".Length..].Trim();
        if (value.StartsWith('[')) return value.Trim('[', ']').Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (value.Length > 0) return [value];
        return job.Skip(at + 1).TakeWhile(l => l.StartsWith("      - ", StringComparison.Ordinal)).Select(l => l.Trim()[2..].Trim()).ToList();
    }

    /// <summary>
    /// A line that names the script: its file name, in any case (Windows finds it so), as <c>ci/&lt;script&gt;</c>,
    /// <c>./&lt;script&gt;</c> in a step run from the ci folder or alone, but not as a part of a longer name (such as the
    /// <c>&lt;script&gt;.out</c> its step saves).
    /// </summary>
    private static Regex Names(string script) => new($@"(?<![\w.-]){Regex.Escape(script)}(?![\w.-])", RegexOptions.IgnoreCase);

    /// <summary>The step that checks the output of a listed script's step, as the workflow must hold it, line by line.</summary>
    internal static List<string> Verification(string script)
    {
        var lines = new List<string> { $"      - name: Check that {script} ran each check that ci/expected-checks.txt lists for it" };
        if (Conditions.TryGetValue(script, out string? condition)) lines.Add($"        if: {condition}");
        lines.AddRange(["        shell: pwsh", "        env:", $"          CI_SCRIPT: {script}", "        run: |"]);
        lines.AddRange(VerificationRun.Select(l => "          " + l));
        return lines;
    }

    /// <summary>
    /// For a script whose verification runs on one leg of its job's matrix (<see cref="Conditions"/>), the step after it,
    /// as the workflow must hold it, line by line: on every other leg it looks for the line Complete-Assertions prints when
    /// no check failed (<see cref="PassedLineRun"/>). Null for any other script.
    /// </summary>
    internal static List<string>? PassedLineCheck(string script)
    {
        if (!Conditions.TryGetValue(script, out string? condition)) return null;
        var leg = Regex.Match(condition, @"^(matrix\.[\w-]+) == ('[^']*')$");
        Assert.True(leg.Success, $"the condition of {script}'s verification is not a leg of a matrix: {condition}");
        var lines = new List<string>
        {
            $"      - name: Check that {script} said that no check failed",
            $"        if: {leg.Groups[1].Value} != {leg.Groups[2].Value}",
            "        shell: pwsh", "        env:", $"          CI_SCRIPT: {script}", "        run: |",
        };
        lines.AddRange(PassedLineRun.Select(l => "          " + l));
        return lines;
    }

    /// <summary>
    /// Why a step of the <paramref name="workflows"/> that names one of the <paramref name="listed"/> scripts is not
    /// verified, or null when each is. Every line of any workflow file that names the script (<see cref="Names"/>), other
    /// than a comment line (which runs nothing), is in a step this test reads (<see cref="Steps(string)"/>; a line outside
    /// one fails, so a step indented otherwise is not passed over). Every such step is the script's
    /// <see cref="Verification"/> or <see cref="PassedLineCheck"/>, exactly, or names it only in lines that run it as
    /// <c>./ci/&lt;script&gt; ...</c> with its output saved (<c>6>&amp;1 | Tee-Object -FilePath
    /// "$env:RUNNER_TEMP/&lt;script&gt;.out"</c>); the next step of the same job after such a run is its Verification,
    /// exactly, and for a script verified on one leg of its matrix the step after that is its PassedLineCheck, exactly; and
    /// each listed script is run by some step.
    /// </summary>
    internal static string? WhyAStepIsNotVerified(IReadOnlyDictionary<string, string> workflows, IReadOnlyCollection<string> listed)
    {
        foreach (var (file, text) in workflows.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            Steps(text, out var read);
            if (NamedOutside(file, text, read, listed, "a step that this test reads (a line \"      - \" and the lines indented under it)") is { } outside) return outside;
        }
        var files = workflows.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => (File: w.Key, Steps: Steps(w.Value))).ToList();
        foreach (string script in listed)
        {
            var named = Names(script);
            string saved = $@" 6>&1 | Tee-Object -FilePath ""$env:RUNNER_TEMP/{script}.out""";
            var call = new Regex($@"^ +(run: )?\./ci/{Regex.Escape(script)} .*{Regex.Escape(saved)}$");
            int runs = 0;
            foreach (var (file, steps) in files)
            {
                foreach (int i in Enumerable.Range(0, steps.Count).Where(i => steps[i].Lines.Any(named.IsMatch)))
                {
                    // The script's own checks of its output name it, and run nothing.
                    if (steps[i].Lines.SequenceEqual(Verification(script)) || PassedLineCheck(script) is { } own && steps[i].Lines.SequenceEqual(own)) continue;
                    runs++;
                    string name = steps[i].Lines[0].Trim();
                    if (!steps[i].Lines.Where(l => named.IsMatch(l)).All(l => call.IsMatch(l)))
                        return $"the step \"{name}\" of {file} names {script} other than in a line that runs it as ./ci/{script} with its output saved for its verification:{saved}";
                    if (i + 1 >= steps.Count || steps[i + 1].Job != steps[i].Job || !steps[i + 1].Lines.SequenceEqual(Verification(script)))
                        return $"the step \"{name}\" of {file} that runs {script} is not followed by its verification:{Environment.NewLine}{string.Join(Environment.NewLine, Verification(script))}";
                    if (PassedLineCheck(script) is { } passed && (i + 2 >= steps.Count || steps[i + 2].Job != steps[i].Job || !steps[i + 2].Lines.SequenceEqual(passed)))
                        return $"the step \"{name}\" of {file} that runs {script} is not followed, after its verification, by the check of its other legs:{Environment.NewLine}{string.Join(Environment.NewLine, passed)}";
                }
            }
            if (runs == 0) return $"no step of the workflows runs {script}";
        }
        return null;
    }

    /// <summary>
    /// Why a job of the <paramref name="workflows"/> that names one of the <paramref name="listed"/> scripts
    /// (<see cref="Names"/>) may skip it, or go on after it failed, or null when none may: every line that names a listed
    /// script, other than a comment line, is in a job this test reads (<see cref="Jobs(string)"/>; a line outside one
    /// fails); the job has no <c>if:</c> (the soak's, as <see cref="JobConditions"/> holds it, aside), no job it needs, at
    /// any depth, has one, no line of the job goes on after a failure (<c>continue-on-error</c>), and a script whose
    /// verification runs on one leg of the job's matrix (<see cref="Conditions"/>) has that leg in the matrix, which
    /// nothing includes or excludes.
    /// </summary>
    internal static string? WhyAJobMaySkipAListedScript(IReadOnlyDictionary<string, string> workflows, IReadOnlyCollection<string> listed)
    {
        foreach (var (file, text) in workflows.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            var jobs = Jobs(text, out var read);
            if (NamedOutside(file, text, read, listed, "a job that this test reads (a line \"  <name>:\" under jobs: and the lines under it)") is { } outside) return outside;
            foreach (var (name, lines) in jobs)
            {
                var scripts = listed.Where(s => lines.Any(Names(s).IsMatch)).ToList();
                if (scripts.Count == 0) continue;
                string what = $"the job {name} of {file}, which runs {string.Join(" and ", scripts)},";
                if (JobKey(lines, "if") is { } condition && scripts.Any(s => !JobConditions.TryGetValue(s, out string? allowed) || allowed != condition))
                    return $"{what} runs only if {condition}";
                if (lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s+continue-on-error:")) is { } goesOn)
                    return $"{what} goes on after a failure: {goesOn.Trim()}";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var needed = new Queue<string>(Needs(lines));
                while (needed.TryDequeue(out string? need))
                {
                    if (!seen.Add(need)) continue;
                    if (!jobs.TryGetValue(need, out var other)) return $"{what} needs {need}, which is not a job of {file}";
                    if (JobKey(other, "if") is { } skip) return $"{what} needs {need}, which runs only if {skip}";
                    foreach (string next in Needs(other)) needed.Enqueue(next);
                }
                foreach (string script in scripts)
                {
                    if (!Conditions.TryGetValue(script, out string? leg)) continue;
                    var wanted = Regex.Match(leg, @"^matrix\.([\w-]+) == '([^']*)'$");
                    Assert.True(wanted.Success, $"the condition of {script}'s verification is not a leg of a matrix: {leg}");
                    var values = lines.Select(l => Regex.Match(l, $@"^\s+{Regex.Escape(wanted.Groups[1].Value)}: \[(.*)\]\s*$")).Where(m => m.Success).ToList();
                    if (values.Count != 1 || !values[0].Groups[1].Value.Split(',').Select(v => v.Trim()).Contains(wanted.Groups[2].Value))
                        return $"{what} has no {wanted.Groups[2].Value} leg in its matrix's {wanted.Groups[1].Value}, where {script}'s pinned checks run";
                    if (lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s+(include|exclude):")) is { } legs)
                        return $"{what} changes the legs of its matrix ({legs.Trim()}), where {script}'s pinned checks run";
                }
            }
        }
        return null;
    }

    private static List<string> Listed() => Entries(List()).Entries.Select(e => e.Script).Distinct().Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void EveryStepThatNamesAListedScriptRunsItSavedAndVerifiedOrChecksItsOutput()
    {
        Assert.Equal(["agentic-soak.ps1", "installer-e2e.ps1", "m4-ui.ps1", "soak.ps1"], Listed());
        Assert.Contains("ci.yml", Workflows().Keys);
        Assert.Null(WhyAStepIsNotVerified(Workflows(), Listed()));
    }

    /// <summary>
    /// Each of these changes to the workflows fails the test above: a listed script's step with no verification after it,
    /// or with another step between them; its output not saved; the verification of another script after it; the soak's
    /// verification on every leg, or the UI test's on only some; the soak's check of its other legs taken out, or made to
    /// run on the ui leg; a new step that runs a listed script with no verification after it, that names it in another
    /// case, or that runs it from the ci folder by its file name alone; the same in a workflow file of its own; and a step
    /// there that the step reader does not read (its list indented level with <c>steps:</c>, which GitHub reads too), also
    /// one that runs it from the ci folder.
    /// </summary>
    [Theory]
    [InlineData("its verification taken out", "is not followed by its verification")]
    [InlineData("another step before its verification", "is not followed by its verification")]
    [InlineData("its output not saved", "names m4-ui.ps1 other than in a line that runs it as ./ci/m4-ui.ps1 with its output saved")]
    [InlineData("another script's verification after it", "is not followed by its verification")]
    [InlineData("the soak's verification on every leg", "that runs soak.ps1 is not followed by its verification")]
    [InlineData("a condition on the UI test's verification", "that runs m4-ui.ps1 is not followed by its verification")]
    [InlineData("the soak's check of its other legs taken out", "that runs soak.ps1 is not followed, after its verification, by the check of its other legs")]
    [InlineData("the soak's check of its other legs on the ui leg", "that runs soak.ps1 is not followed, after its verification, by the check of its other legs")]
    [InlineData("a new step that runs a listed script", "\"- name: UI again\" of ci.yml names m4-ui.ps1 other than in a line that runs it")]
    [InlineData("a new step that names a listed script in another case", "\"- name: UI again\" of ci.yml names m4-ui.ps1 other than in a line that runs it")]
    [InlineData("a new step that runs and saves a listed script", "\"- name: UI again\" of ci.yml that runs m4-ui.ps1 is not followed")]
    [InlineData("a new workflow file that runs a listed script", "\"- name: UI again\" of other.yml names m4-ui.ps1 other than in a line that runs it")]
    [InlineData("a new step that runs a listed script from the ci folder", "\"- name: UI again\" of ci.yml names m4-ui.ps1 other than in a line that runs it")]
    [InlineData("a new workflow file whose steps are indented level with steps:", "line 10 of other.yml names m4-ui.ps1 outside a step that this test reads")]
    [InlineData("a new workflow file whose steps are indented level with steps:, run from the ci folder", "line 11 of other.yml names m4-ui.ps1 outside a step that this test reads")]
    public void TheWorkflowFailsOnEachStepThatIsNotVerified(string change, string reason)
    {
        var workflows = Workflows();
        string workflow = workflows["ci.yml"].Replace("\r\n", "\n", StringComparison.Ordinal);
        string ui = string.Join("\n", Verification("m4-ui.ps1")) + "\n";
        string soakLegs = string.Join("\n", PassedLineCheck("soak.ps1")!) + "\n";
        const string run = "-Out \"$env:RUNNER_TEMP/ui\" 6>&1 | Tee-Object -FilePath \"$env:RUNNER_TEMP/m4-ui.ps1.out\"\n";
        const string upload = "      - name: Upload screenshots\n        if: always()\n";
        const string again = "      - name: UI again\n        shell: pwsh\n        run: ./ci/m4-ui.ps1 -Exe x -Leaker y -Out z\n";
        const string fromCi = "      - name: UI again\n        shell: pwsh\n        working-directory: ci\n        run: ./m4-ui.ps1 -Exe x -Leaker y -Out z\n";
        workflows["ci.yml"] = change switch
        {
            "its verification taken out" => Once(workflow, ui, ""),
            "another step before its verification" => Once(workflow, run + ui, run + "      - name: Wait\n        run: Start-Sleep -Seconds 1\n" + ui),
            "its output not saved" => Once(workflow, run, "-Out \"$env:RUNNER_TEMP/ui\"\n"),
            "another script's verification after it" => Once(workflow, ui, ui.Replace("m4-ui.ps1", "soak.ps1", StringComparison.Ordinal)),
            "the soak's verification on every leg" => Once(workflow, "        if: matrix.mode == 'ui'\n        shell: pwsh\n        env:\n          CI_SCRIPT: soak.ps1\n",
                "        shell: pwsh\n        env:\n          CI_SCRIPT: soak.ps1\n"),
            "a condition on the UI test's verification" => Once(workflow, ui, ui.Replace("\n        shell: pwsh\n", "\n        if: github.event_name == 'push'\n        shell: pwsh\n", StringComparison.Ordinal)),
            "the soak's check of its other legs taken out" => Once(workflow, soakLegs, ""),
            "the soak's check of its other legs on the ui leg" => Once(workflow, soakLegs, soakLegs.Replace("if: matrix.mode != 'ui'", "if: matrix.mode == 'ui'", StringComparison.Ordinal)),
            "a new step that runs a listed script" => Once(workflow, upload, again + upload),
            "a new step that names a listed script in another case" => Once(workflow, upload, again.Replace("./ci/m4-ui.ps1", "./CI/m4-ui.ps1", StringComparison.Ordinal) + upload),
            "a new step that runs and saves a listed script" => Once(workflow, upload,
                "      - name: UI again\n        shell: pwsh\n        run: ./ci/m4-ui.ps1 -Exe x 6>&1 | Tee-Object -FilePath \"$env:RUNNER_TEMP/m4-ui.ps1.out\"\n" + upload),
            "a new step that runs a listed script from the ci folder" => Once(workflow, upload, fromCi + upload),
            "a new workflow file that runs a listed script" or "a new workflow file whose steps are indented level with steps:"
                or "a new workflow file whose steps are indented level with steps:, run from the ci folder" => workflow,
            _ => throw new ArgumentException("no such change: " + change),
        };
        const string other = "name: Other\non:\n  workflow_dispatch:\njobs:\n  again:\n    runs-on: windows-latest\n    steps:\n";
        static string LevelWithSteps(string step) => string.Join("\n", step.Split('\n').Select(l => l.Length >= 2 ? l[2..] : l));
        if (change == "a new workflow file that runs a listed script") workflows["other.yml"] = other + again;
        if (change == "a new workflow file whose steps are indented level with steps:") workflows["other.yml"] = other + LevelWithSteps(again);
        if (change == "a new workflow file whose steps are indented level with steps:, run from the ci folder") workflows["other.yml"] = other + LevelWithSteps(fromCi);
        Assert.Null(WhyAStepIsNotVerified(Workflows(), Listed()));
        string? why = WhyAStepIsNotVerified(workflows, Listed());
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    [Fact]
    public void EveryJobThatNamesAListedScriptHasNoConditionButTheSoaksAndFailsOnAFailure()
    {
        Assert.Null(WhyAJobMaySkipAListedScript(Workflows(), Listed()));
        // The soak's job runs on demand and on a release tag only, and the release needs it.
        Assert.Equal(JobConditions["soak.ps1"], JobKey(Jobs(Workflows()["ci.yml"])["soak"], "if"));
        Assert.Contains("soak", Needs(Jobs(Workflows()["ci.yml"])["sign"]));
        Assert.Equal(JobConditions["agentic-soak.ps1"], JobKey(Jobs(Workflows()["ci.yml"])["agentic-soak"], "if"));
        Assert.Contains("agentic-soak", Needs(Jobs(Workflows()["ci.yml"])["sign"]));
    }

    /// <summary>
    /// Each of these changes to the workflow fails the test above, each a way for a listed script's job, or the leg of it
    /// that verifies the script, not to run while the workflow still passes: a condition on the UI test's job, another
    /// condition on the soak's, a condition on the build job that each of them needs, the soak's matrix without its ui leg
    /// or with that leg excluded, a job or a step of it that goes on after a failure, and a new workflow file whose job,
    /// indented further than this test reads jobs, runs a listed script under a condition (also from the ci folder, by its
    /// file name alone).
    /// </summary>
    [Theory]
    [InlineData("a job indented further in a new workflow file", "line 10 of other.yml names m4-ui.ps1 outside a job that this test reads")]
    [InlineData("a job indented further in a new workflow file, run from the ci folder", "line 11 of other.yml names m4-ui.ps1 outside a job that this test reads")]
    [InlineData("a condition on the UI test's job", "the job ui of ci.yml, which runs m4-ui.ps1, runs only if false")]
    [InlineData("another condition on the soak's job", "the job soak of ci.yml, which runs soak.ps1, runs only if github.event_name == 'workflow_dispatch'")]
    [InlineData("a condition on the job they need", "needs build, which runs only if false")]
    [InlineData("the soak's matrix without its ui leg", "has no ui leg in its matrix's mode")]
    [InlineData("the soak's ui leg excluded", "changes the legs of its matrix (exclude:)")]
    [InlineData("a job that goes on after a failure", "the job installer of ci.yml, which runs installer-e2e.ps1, goes on after a failure")]
    [InlineData("a step that goes on after a failure", "the job ui of ci.yml, which runs m4-ui.ps1, goes on after a failure")]
    public void AJobFailsTheTestWhenItMaySkipItsListedScript(string change, string reason)
    {
        Assert.Null(WhyAJobMaySkipAListedScript(Workflows(), Listed()));
        var workflows = Workflows();
        string workflow = workflows["ci.yml"].Replace("\r\n", "\n", StringComparison.Ordinal);
        const string dispatchOrTag = "    if: github.event_name == 'workflow_dispatch' || startsWith(github.ref, 'refs/tags/v')\n    needs: build\n";
        workflows["ci.yml"] = change switch
        {
            "a condition on the UI test's job" => Once(workflow, "  ui:\n    needs: build\n", "  ui:\n    if: false\n    needs: build\n"),
            "another condition on the soak's job" => Once(workflow, "  soak:\n" + dispatchOrTag, "  soak:\n    if: github.event_name == 'workflow_dispatch'\n    needs: build\n"),
            "a condition on the job they need" => Once(workflow, "  build:\n", "  build:\n    if: false\n"),
            "the soak's matrix without its ui leg" => Once(workflow, "        mode: [headless, ui]\n", "        mode: [headless]\n"),
            "the soak's ui leg excluded" => Once(workflow, "        mode: [headless, ui]\n", "        mode: [headless, ui]\n        exclude:\n          - mode: ui\n"),
            "a job that goes on after a failure" => Once(workflow, "  installer:\n    needs: build\n", "  installer:\n    needs: build\n    continue-on-error: true\n"),
            "a step that goes on after a failure" => Once(workflow, "      - name: UI smoke test + screenshots\n        shell: pwsh\n",
                "      - name: UI smoke test + screenshots\n        continue-on-error: true\n        shell: pwsh\n"),
            "a job indented further in a new workflow file" or "a job indented further in a new workflow file, run from the ci folder" => workflow,
            _ => throw new ArgumentException("no such change: " + change),
        };
        const string further = "name: Other\non:\n  workflow_dispatch:\njobs:\n    again:\n        if: false\n        runs-on: windows-latest\n        steps:\n" +
            "            - name: UI again\n";
        if (change == "a job indented further in a new workflow file")
            workflows["other.yml"] = further + "              run: ./ci/m4-ui.ps1 -Exe x 6>&1 | Tee-Object -FilePath \"$env:RUNNER_TEMP/m4-ui.ps1.out\"\n";
        if (change == "a job indented further in a new workflow file, run from the ci folder")
            workflows["other.yml"] = further + "              working-directory: ci\n              run: ./m4-ui.ps1 -Exe x\n";
        string? why = WhyAJobMaySkipAListedScript(workflows, Listed());
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    /// <summary>
    /// The verification fails the job when a marker is missing, or when the script ended before Complete-Assertions said
    /// that no check failed; on the legs of the soak's matrix where it does not run, the step after it fails the job in the
    /// second case. Both run only in CI, so this holds their text, in every such step of every workflow file: each reads the
    /// output that the script's step saved; the verification takes the list's ids for that script and throws (which fails
    /// a pwsh step) when the list holds none for it, or when any of them has no <c>CHECK-RAN: &lt;id&gt;</c> line in that
    /// output, naming each such id; and each throws when no line of that output ends "<c> : all assertions passed</c>". A
    /// step that only warned, or that looked for the ids it found rather than the ones missing, fails this test.
    /// </summary>
    [Fact]
    public void TheVerificationFailsTheJobOnAMissingMarker()
    {
        var checks = Workflows().SelectMany(w => Steps(w.Value)).Where(s => s.Lines.Any(l => l.StartsWith("          CI_SCRIPT: ", StringComparison.Ordinal)))
            .Select(s => (Script: s.Lines.Single(l => l.StartsWith("          CI_SCRIPT: ", StringComparison.Ordinal))[21..], s.Lines)).ToList();
        var verifications = checks.Where(c => c.Lines.SequenceEqual(Verification(c.Script))).ToList();
        var otherLegs = checks.Where(c => PassedLineCheck(c.Script) is { } passed && c.Lines.SequenceEqual(passed)).ToList();
        Assert.Equal(checks.Count, verifications.Count + otherLegs.Count); // no other step sets CI_SCRIPT (a line of its env:)
        Assert.Equal(Listed(), verifications.Select(c => c.Script).Order(StringComparer.Ordinal));
        Assert.Equal(["soak.ps1"], otherLegs.Select(c => c.Script));
        foreach (var (_, lines) in verifications)
        {
            var run = lines.SkipWhile(l => l != "        run: |").Skip(1).Select(l => l.Trim()).ToList();
            Assert.Equal(VerificationRun, run);
            Assert.StartsWith("$ran = @(Get-Content -LiteralPath \"$env:RUNNER_TEMP/$env:CI_SCRIPT.out\"", run[0]); // what the script's step saved
            Assert.Equal("if ($expected.Count -eq 0) { throw \"ci/expected-checks.txt lists no check for $env:CI_SCRIPT\" }", run[2]);
            Assert.Equal("$missing = @($expected | Where-Object { \"CHECK-RAN: $_\" -notin $ran })", run[3]);
            Assert.Equal("if ($missing.Count -gt 0) { throw \"$env:CI_SCRIPT did not run these checks: $($missing -join ', ')\" }", run[4]);
            Assert.Equal("if (@($ran -match ' : all assertions passed$').Count -eq 0) { throw \"$env:CI_SCRIPT ended before Complete-Assertions said that no check failed\" }", run[5]);
        }
        foreach (var (_, lines) in otherLegs)
        {
            var run = lines.SkipWhile(l => l != "        run: |").Skip(1).Select(l => l.Trim()).ToList();
            Assert.Equal(PassedLineRun, run);
            Assert.StartsWith("$ran = @(Get-Content -LiteralPath \"$env:RUNNER_TEMP/$env:CI_SCRIPT.out\"", run[0]);
            Assert.Equal("if (@($ran -match ' : all assertions passed$').Count -eq 0) { throw \"$env:CI_SCRIPT ended before Complete-Assertions said that no check failed\" }", run[1]);
        }
    }
}
