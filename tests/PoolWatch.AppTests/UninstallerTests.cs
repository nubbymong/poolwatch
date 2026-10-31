using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Changes;
using PoolWatch.Engine.Changes;

namespace PoolWatch.AppTests;

/// <summary>
/// The uninstaller's side of putting everything back (installer/PoolWatch.iss): it puts back every change by default and
/// keeps them only when told to; a silent uninstall and /DELETEDATA never ask; started by hand it first says what it puts
/// back, read from PoolWatch's own list (<c>--changes-dump</c>, <see cref="ChangeLedger.ToJson"/>), line by line; and
/// afterwards it says what it put back, read from uninstall-summary.txt (<see cref="Program.UninstallSummary"/>). These
/// tests hold the script's choices, its file handling and the shapes of the two files it reads, with a reader written the
/// way the script's is. The script itself is first compiled and run by CI (the installer build, and its end-to-end test,
/// which uninstalls silently only); the reader here is a copy of the script's, held to the script's own text
/// (<see cref="TheReaderHereIsTheScriptsOwn"/>): change the two together.
/// </summary>
public sealed class UninstallerTests
{
    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return File.ReadAllText(Path.Combine(dir.FullName, "installer", "PoolWatch.iss"));
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The script's [Code] with every run of white space made one space, so the tests read it as statements.</summary>
    private static string Code() => Regex.Replace(Script()[Script().IndexOf("[Code]", StringComparison.Ordinal)..], @"\s+", " ");

    /// <summary>One function or procedure of the script, up to the next one, with every run of white space made one space.</summary>
    private static string Body(string name)
    {
        string script = Script();
        var start = Regex.Match(script, $@"(?m)^(function|procedure) {name}\b");
        Assert.True(start.Success, name + " is not in the script");
        var next = Regex.Match(script[(start.Index + start.Length)..], @"(?m)^(function|procedure) ");
        return Regex.Replace(next.Success ? script.Substring(start.Index, start.Length + next.Index) : script[start.Index..], @"\s+", " ");
    }

    /// <summary>A string constant of the script's [Code] (a Pascal string, with no quote inside).</summary>
    private static string Constant(string name) => Regex.Match(Script(), $@"(?m)^\s*{name} = '([^']*)';").Groups[1].Value;

    private static int Number(string name) => int.Parse(Regex.Match(Script(), $@"(?m)^\s*{name} = (\d+);").Groups[1].Value);

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

    private static ActiveChange Guard(bool canUndo = true) =>
        new(ChangeIds.TokenGuard, ChangeKind.TokenGuard, null, ChangeWords.TokenGuard, canUndo, ChangeWords.TokenGuardNote) { Title = ChangeWords.TokenGuardTitle };

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
        // The guard's Windows setting lasts until you sign out after it is turned off: the uninstaller that turns it off
        // says so, under its list before it asks and in what it says once it put everything back.
        var shown = ReadAsTheUninstallerDoes(ChangeLedger.ToJson([Guard(), Row(ChangeIds.LogonTask, ChangeKind.LogonTask, ChangeWords.LogonTask)]));
        Assert.Equal([ChangeWords.TokenGuard, ChangeWords.LogonTask], shown.PutsBack);
        Assert.Equal([ChangeWords.TokenGuardSettingStays], shown.Caveats);
        Assert.Contains("    \"putBackCaveat\": \"The token-leak guard's Windows setting stays until you sign out.\"", ChangeLedger.ToJson([Guard()]));
        // Only a change it puts back has one: the guard's row that could not be read has none, and no other kind does.
        Assert.Null(Guard(canUndo: false).PutBackCaveat);
        Assert.Empty(ReadAsTheUninstallerDoes(ChangeLedger.ToJson([Guard(canUndo: false)])).Caveats);
        Assert.All(Enum.GetValues<ChangeKind>().Where(k => k != ChangeKind.TokenGuard), k => Assert.Null(Row("x", k, "x").PutBackCaveat));

        Assert.Contains("Text := 'These are the changes PoolWatch made on this PC that are still in effect. Uninstalling puts each one back ' + " +
                        "'as it was before PoolWatch changed it:' + #13#10 + PutBackList + MoreLine(PutBackCount); " +
                        "if CaveatList <> '' then Text := Text + #13#10 + CaveatList;", Body("PutBackAnswer"));
        Assert.Contains("if (Count <> 0) and (CaveatList <> '') then Result := Result + #13#10 + CaveatList " +
                        "else if (Count <> 0) and not ListRead then Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;", Body("PutBackDone"));
        Assert.Contains("Log('After it is put back: ' + Caveat);", Body("ReadChangeList"));
        // Also when something could not be put back (an uninstall with /DELETEDATA asked nothing first), and, when PoolWatch
        // could not list its changes, as a line that says it only if the guard was on.
        Assert.Contains("if CaveatList <> '' then Result := Result + #13#10 + CaveatList " +
                        "else if not ListRead then Result := Result + #13#10#13#10 + CaveatIfGuardWasOn;", Body("NotAllPutBack"));
        Assert.Contains("Uninstalling puts back every change it recorded.' + #13#10#13#10 + CaveatIfGuardWasOn;", Body("PutBackAnswer")); // after a blank line, as elsewhere
        // In the list's own words, said of a guard that may have been on.
        Assert.Equal("If the token-leak guard was on, its Windows setting stays until you sign out.", Constant("CaveatIfGuardWasOn"));
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

        string read = Body("ReadChangeList");
        Assert.Contains("Log('Uninstalling puts back: ' + What); AddShown(PutBackList, PutBackCount, Shorten(What, MaxNameShown));", read);
        Assert.Contains("Log('Uninstalling cannot put back: ' + What + '. ' + Leaves); AddShown(LeftList, LeftCount, Shorten(What, MaxNameShown) + '. ' + Leaves);", read);
        Assert.Contains("List := List + #13#10 + '- ' + Shorten(Line, MaxLineShown);", Body("AddShown"));
        Assert.Contains("if Length(S) > Max then Result := Copy(S, 1, Max - 3) + '...' else Result := S;", Body("Shorten"));
    }

    [Fact]
    public void TheScriptReadsTheLinesTheListIsWrittenWith()
    {
        Assert.Equal("    \"what\": \"", Constant("WhatKey"));
        Assert.Equal("    \"uninstallLeaves\": \"", Constant("LeavesKey"));
        Assert.Equal("    \"putBackCaveat\": \"", Constant("CaveatKey"));
        Assert.Equal("uninstall-list.json", Constant("ListFileName"));
        string json = ChangeLedger.ToJson([Row("unread", ChangeKind.Shortcut, "x", canUndo: false, note: "why"), Guard()]);
        var lines = json.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains("  {", lines);
        Assert.Contains("    \"what\": \"x\",", lines);
        Assert.Contains(lines, l => l.StartsWith("    \"uninstallLeaves\": \"why\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("    \"putBackCaveat\": \"", StringComparison.Ordinal));
        Assert.Contains("  },", lines);
        Assert.Contains("  }", lines);
        // The list comes from the installed PoolWatch, into its own admin-only data folder, and is deleted once read.
        string code = Code();
        Assert.Contains("ListFile := DataDir + '\\' + ListFileName; if not Exec(ExpandConstant('{app}\\PoolWatch.exe'), '--changes-dump \"' + ListFile + '\"'", code);
        Assert.Contains("ListRead := LoadStringsFromFile(ListFile, Lines); DeleteFile(ListFile);", code);
    }

    [Fact]
    public void WhenPoolWatchMadeNoListTheUninstallerTouchesNothingInTheDataFolder()
    {
        // No list (PoolWatch could not start, or refused a data folder it could not make safe): that folder was not
        // secured this time, so the uninstaller neither reads nor deletes anything there; it says so in its log and goes on.
        string read = Body("ReadChangeList");
        int failed = read.IndexOf("if Code <> 0 then begin Log(Format('PoolWatch could not list its changes (exit %d).', [Code])); Exit; end;", StringComparison.Ordinal);
        Assert.True(failed > 0, "the failure branch is only a log line and Exit");
        // The one file operation in the data folder: deleting the list it read, after PoolWatch made it (exit 0).
        Assert.Single(Regex.Matches(read, @"\bDeleteFile\("));
        Assert.Single(Regex.Matches(read, @"\bLoadStringsFromFile\("));
        Assert.True(read.IndexOf("DeleteFile(", StringComparison.Ordinal) > failed && read.IndexOf("LoadStringsFromFile(", StringComparison.Ordinal) > failed,
            "the list is read and deleted only after the failure branch has exited");
        foreach (var op in new[] { "DelTree(", "RenameFile(", "SaveStringToFile(", "SaveStringsToFile(", "FileCopy(", "RemoveDir(", "CreateDir(", "ForceDirectories(" })
            Assert.DoesNotContain(op, read);
    }

    [Fact]
    public void TheReaderHereIsTheScriptsOwn()
    {
        // The C# reader above copies these parts of the script: a change to either side shows here.
        Assert.Contains("function JsonText(const S: String; P: Integer): String; var C: Char; begin Result := ''; while P <= Length(S) do begin " +
                        "C := S[P]; if C = '\"' then Exit; if C = '\\' then begin P := P + 1; if P > Length(S) then Exit; C := S[P]; " +
                        "if C = 'u' then begin Result := Result + ' '; P := P + 4; end " +
                        "else if (C = 'b') or (C = 'f') or (C = 'n') or (C = 'r') or (C = 't') then Result := Result + ' ' " +
                        "else Result := Result + C; end else Result := Result + C; P := P + 1; end; end;", Body("JsonText"));
        // White space is made one space here, so the script's "  {" reads as " {".
        Assert.Contains("Line := TrimRight(Lines[I]); if Line = ' {' then begin What := ''; Leaves := ''; Caveat := ''; end " +
                        "else if Copy(Line, 1, Length(WhatKey)) = WhatKey then What := JsonText(Line, Length(WhatKey) + 1) " +
                        "else if Copy(Line, 1, Length(LeavesKey)) = LeavesKey then Leaves := JsonText(Line, Length(LeavesKey) + 1) " +
                        "else if Copy(Line, 1, Length(CaveatKey)) = CaveatKey then Caveat := JsonText(Line, Length(CaveatKey) + 1) " +
                        "else if ((Line = ' }') or (Line = ' },')) and (What <> '') then begin if Leaves = '' then begin", Body("ReadChangeList"));
        Assert.Contains("Shown := Shorten(Caveat, MaxLineShown); if Pos(Shown, CaveatList) = 0 then CaveatList := CaveatList + #13#10 + Shown;", Body("ReadChangeList"));
        Assert.Contains("Count := Count + 1; if Count <= MaxShown then", Body("AddShown"));
    }

    // ---------------------------------------------------------------- what it says after the cleanup

    [Fact]
    public void TheNoticesAfterTheCleanupReadTheLinesPoolWatchWrites()
    {
        // uninstall-summary.txt is PoolWatch's (Program.UninstallSummary); the script picks its lines by two prefixes.
        string putBack = Constant("PutBackPrefix"), notPutBack = Constant("NotPutBackPrefix");
        Assert.Equal(("Put back: ", "Could not put back: "), (putBack, notPutBack));
        var done = Row("service:a", ChangeKind.Service, "Turned off Example Device Service (ExampleSvc)");
        var failed = Row("task:b", ChangeKind.ScheduledTask, "Turned off the scheduled task Vendor Updater");
        string summary = Program.UninstallSummary([done, failed], null,
            [(done, new UndoResult(true, "It is back.")), (failed, new UndoResult(false, "Access is denied."))], new DateTime(2026, 10, 30, 9, 0, 0));
        var lines = summary.Split(Environment.NewLine);

        // As SummaryLines does: each line that starts with the prefix, without it.
        Assert.Equal([done.What], lines.Where(l => l.StartsWith(putBack, StringComparison.Ordinal)).Select(l => l[putBack.Length..]));
        Assert.Equal([failed.What + ". Access is denied."], lines.Where(l => l.StartsWith(notPutBack, StringComparison.Ordinal)).Select(l => l[notPutBack.Length..]));
        Assert.Contains("if Copy(Lines[I], 1, Length(Prefix)) = Prefix then AddShown(Shown, Count, Shorten(Copy(Lines[I], Length(Prefix) + 1, Length(Lines[I])), MaxSummaryShown));", Body("SummaryLines"));
        Assert.Contains("SummaryLines(DataDir, PutBackPrefix, Count)", Body("PutBackDone"));
        Assert.Contains("SummaryLines(DataDir, NotPutBackPrefix, Count)", Body("NotAllPutBack"));
        // The prefixes are written once, as the constants this test reads.
        Assert.Single(Regex.Matches(Code(), "'Put back: '"));
        Assert.Single(Regex.Matches(Code(), "'Could not put back: '"));
        // Nothing was changed: PoolWatch says so in a line of its own, which neither prefix picks.
        string none = Program.UninstallSummary([], null, [], new DateTime(2026, 10, 30, 9, 0, 0));
        Assert.DoesNotContain(none.Split(Environment.NewLine), l => l.StartsWith(putBack, StringComparison.Ordinal) || l.StartsWith(notPutBack, StringComparison.Ordinal));
    }

    [Fact]
    public void WhatItSaysAfterTheCleanupNamesWhatItPutBackAndWhereToLookWhenItCannotReadIt()
    {
        string done = Body("PutBackDone");
        Assert.Contains("if Count = 0 then Result := 'PoolWatch had no changes on this PC to put back.' " +
                        "else if Count < 0 then Result := 'PoolWatch put back every change it recorded.' " +
                        "else Result := 'PoolWatch put back each of these changes:' + #13#10 + Lines;", done);
        // A summary that cannot be read is not taken for an empty one (-1, not 0).
        Assert.Contains("Count := -1; if not LoadStringsFromFile(", Body("SummaryLines"));
        Assert.Contains("Exit; Count := 0; for I := 0 to", Body("SummaryLines"));
        string notAll = Body("NotAllPutBack");
        Assert.Contains("if Count > 0 then Result := 'Some changes could not be put back:' + #13#10 + Lines + #13#10#13#10 " +
                        "else Result := 'Some changes could not be put back. ';", notAll);
        Assert.Contains("'uninstall-summary.txt there says what was put back and what was not, and why.'", notAll);
    }

    // ---------------------------------------------------------------- the choice

    [Fact]
    public void AnUninstallPutsEverythingBackUnlessToldToKeepItAndNeverAsksWhenSilentOrDeletingTheData()
    {
        string code = Code();
        // /KEEPCHANGES keeps; a silent uninstall (/SILENT, /VERYSILENT) and /DELETEDATA put it all back without asking;
        // only an uninstall started by hand asks.
        Assert.Contains("function PutBackChoice: Integer; begin if HasSwitch('KEEPCHANGES') then Result := ChoiceKeep " +
                        "else if UninstallSilent or HasSwitch('DELETEDATA') then Result := ChoicePutBack else Result := ChoiceAsk; end;", code);
        Assert.Contains("case PutBackChoice of ChoiceKeep: Result := True; ChoicePutBack: Result := False; else Result := not PutBackAnswer; end;", code);
        // Asked, putting it all back is the first choice (the task dialog's default) and anything but Keep puts it back,
        // a box that could not be shown included.
        Assert.Contains("Labels[0] := 'Put them back (recommended)'", code);
        Assert.Contains("Labels[1] := 'Keep my changes'", code);
        Assert.Contains("try Result := TaskDialogMsgBox('Put back what PoolWatch changed?', Text, mbConfirmation, MB_YESNO, Labels, 0) <> IDNO; " +
                        "except Log('The question could not be shown (' + GetExceptionMessage + '): every change is put back, the default.'); Result := True; end;", code);
        // With nothing to put back, nothing is asked.
        Assert.Contains("Result := True; if ListRead and (PutBackCount = 0) then Exit;", code);
        // The list is read first, then the choice is made and passed to the cleanup, which puts back all (or, kept, only
        // the logon task and the shortcut: Program.UndoSelection).
        Assert.Matches(@"usAppMutexCheck: begin \{[^}]*\} ReadChangeList\(DataDir\); KeepChanges := KeepChangesWanted; .*Code := RunCleanup\(KeepChanges\);", code);
        Assert.Contains("if KeepChanges then Params := Params + ' --keep-changes';", code);
        Assert.Null(Program.UndoSelection(keepChanges: false)); // everything
        // The data folder goes only when every change was put back.
        Assert.Contains("ChangesPutBack := (Code = 0) and not KeepChanges;", code);
        Assert.Contains("usPostUninstall: if ChangesPutBack then if DeleteDataWanted(DataDir) then DelTree(DataDir, True, True, True);", code);
    }

    [Fact]
    public void ASilentUninstallShowsNoBoxAfterTheCleanup()
    {
        // Every box after the cleanup is behind "if not UninstallSilent"; the one before it (PoolWatch still running) aborts
        // a silent uninstall instead of asking.
        string code = Code();
        int step = code.IndexOf("usAppMutexCheck:", StringComparison.Ordinal);
        string body = code[step..code.IndexOf("usPostUninstall:", StringComparison.Ordinal)];
        Assert.Contains("while (Code = ExitStillRunning) or CheckForMutexes(MonitorMutex) do begin if UninstallSilent then Abort;", body);
        int guard = body.IndexOf("if not UninstallSilent then begin if Code = ExitNotAllPutBack then MsgBox(", StringComparison.Ordinal);
        Assert.True(guard > 0, "the notices after the cleanup are shown only when someone is there");
        Assert.Equal(3, Regex.Matches(body[guard..], @"MsgBox\(").Count); // the three notices, all inside that guard
        // The data folder's question and the notice after /DELETEDATA: neither when silent.
        string delete = Body("DeleteDataWanted");
        Assert.Equal(2, Regex.Matches(delete, @"MsgBox\(").Count);
        Assert.Contains("if HasSwitch('DELETEDATA') then begin if not UninstallSilent then MsgBox(", delete);
        Assert.Contains("else if UninstallSilent then Result := False else Result := MsgBox(", delete);
    }

    [Fact]
    public void ADeleteDataUninstallStartedByHandSaysWhatItPutBack()
    {
        // Started by hand with /DELETEDATA, it asks nothing about the changes, so once every change is back it says what it
        // put back, and that the data folder goes too; then it deletes the folder, as asked.
        string delete = Body("DeleteDataWanted");
        Assert.Contains("if not UninstallSilent then MsgBox(PutBackDone(DataDir) + #13#10#13#10 + 'PoolWatch''s reports, logs and settings (' + DataDir + ') are deleted too.', " +
                        "mbInformation, MB_OK); Result := True; end", delete);
        // Only reached once every change was put back.
        Assert.Contains("usPostUninstall: if ChangesPutBack then if DeleteDataWanted(DataDir) then", Code());
    }
}
