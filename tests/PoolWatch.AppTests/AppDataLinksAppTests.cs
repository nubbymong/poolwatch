using FsCheck;
using FsCheck.Fluent;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Links;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Ui;
using static PoolWatch.Tests.PropertyInputs;

namespace PoolWatch.AppTests;

/// <summary>
/// A made-up disk for the AppData links finder: real folders and files (a folder is there when something is in it), and
/// links, each by where it is with the target it holds. Every look follows the links on the way as Windows does, and the
/// entry itself is never followed. No test looks at a real link or reads a real PATH.
/// </summary>
internal sealed class FakeLinkSystem : ILinkSystem
{
    private static readonly StringComparer Case = StringComparer.OrdinalIgnoreCase;

    public string AppData { get; init; } = @"C:\Users\someone\AppData\Roaming";
    public string LocalFolder { get; init; } = @"C:\Users\someone\AppData\Local";
    public string Profile { get; init; } = @"C:\Users\someone";
    public List<string> Packages { get; } = ["Claude_5.11492.5.0_x64__pzs8sxrjxfjjc"];
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    public FakeEnvironmentStore Values { get; } = new();
    public IEnvironmentStore Store => Values;
    /// <summary>Each link by where it is, with the target it holds (null: one whose target cannot be read).</summary>
    public Dictionary<string, string?> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Real files and folders, by their real paths: every folder above one is there too.</summary>
    public HashSet<string> Real { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Looked { get; } = new();

    public FakeLinkSystem()
    {
        Env["USERPROFILE"] = Profile;
        Env["APPDATA"] = AppData;
        Env["LOCALAPPDATA"] = LocalFolder;
        Real.Add(AppData + @"\desktop.ini");
        Real.Add(LocalFolder + @"\desktop.ini");
    }

    public IReadOnlyList<string> PackageFullNames() => Packages;
    public IReadOnlyDictionary<string, string> Environment() => Env;

    public LinkLook Look(string path)
    {
        Looked.Add(path);
        string p = path.TrimEnd('\\');
        int cut = p.LastIndexOf('\\');
        if (Resolved(p[..cut]) is not { } parent) return new(LinkState.NotThere);
        string at = parent + p[cut..];
        if (Links.TryGetValue(at, out var target)) return new(LinkState.Link, target);
        return Exists(at) ? new(LinkState.Plain) : new(LinkState.NotThere);
    }

    public string? Resolved(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        string at = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            at += "\\" + parts[i];
            if (Links.TryGetValue(at, out var target))
            {
                if (target is null) return null;
                string full = Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(at)!, target));
                if (full.Equals(at, StringComparison.OrdinalIgnoreCase) || Resolved(full) is not { } really) return null;
                at = really;
            }
        }
        return Exists(at) ? at : null;
    }

    public IReadOnlyList<string> Folders(string folder, int max) =>
        Resolved(folder) is { } at ? Real.Concat(Links.Keys).Select(p => Child(at, p)).OfType<string>().Distinct(Case).Take(max).ToList() : [];

    public int LinksIn(string folder, int max) =>
        Resolved(folder) is { } at ? Links.Keys.Where(l => Path.GetDirectoryName(l)!.Equals(at, StringComparison.OrdinalIgnoreCase)).Take(max).Count() : 0;

    private bool Exists(string p) => p.Length == 2 && p[1] == ':' || Real.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase) || r.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));

    private static string? Child(string folder, string p) =>
        p.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? folder + "\\" + p[(folder.Length + 1)..].Split('\\')[0] : null;

    /// <summary>A junction or link at <paramref name="at"/> to <paramref name="target"/>, with a file in the target so it is there.</summary>
    public void Link(string at, string target, bool targetThere = true)
    {
        Links[at] = target;
        Real.Add(Path.GetDirectoryName(at)! + @"\desktop.ini");
        if (targetThere && Path.IsPathFullyQualified(target)) Real.Add(target + @"\tool.exe");
    }
}

/// <summary>The registry's environment variables, in memory: every write is counted, can be made to fail, and checks the value it expects.</summary>
internal sealed class FakeEnvironmentStore : IEnvironmentStore
{
    public readonly Dictionary<(EnvScope Scope, string Name), EnvValue> Values = new();
    public readonly List<string> Writes = new();
    public int Reads, Broadcasts;
    public string? WriteError;
    /// <summary>Runs right before each write (a test checks what is recorded then).</summary>
    public Action? BeforeWrite;

    public void Set(EnvScope scope, string name, string text, bool expand = false) => Values[(scope, name.ToUpperInvariant())] = new EnvValue(text, expand);

    public string? Text(EnvScope scope, string name) => Read(scope, name)?.Text;

    public EnvValue? Read(EnvScope scope, string name)
    {
        Reads++;
        return Values.TryGetValue((scope, name.ToUpperInvariant()), out var v) ? v : null;
    }

    public string? Write(EnvScope scope, string name, EnvValue? expected, EnvValue? value)
    {
        BeforeWrite?.Invoke();
        Writes.Add($"{scope} {name}");
        if (WriteError is { } error) return error;
        if ((Values.TryGetValue((scope, name.ToUpperInvariant()), out var now) ? now : (EnvValue?)null) != expected) return WindowsEnvironmentStore.ChangedSince;
        if (value is { } v) Values[(scope, name.ToUpperInvariant())] = v;
        else Values.Remove((scope, name.ToUpperInvariant()));
        return null;
    }

    public void Broadcast() => Broadcasts++;
}

/// <summary>
/// The AppData links finder, its two fixes and Put back, on a made-up disk and registry (<see cref="FakeLinkSystem"/>):
/// what is found and only when the Claude or Codex app is installed, a PATH entry replaced alone with its kind kept and
/// recorded before it is written, every refusal (changed since shown, a target that is missing or behind another link
/// under AppData, a record that cannot be saved, a write that fails), the variables of the guided fix, and Put back of
/// each, checked against the registry as it is then. No test changes the real PC.
/// </summary>
public sealed class AppDataLinksAppTests : IDisposable
{
    private readonly ChangeFixture _f = new();
    private readonly FakeLinkSystem _pc = new();
    private const string Codex = @"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin";
    private const string CodexLink = @"C:\Users\someone\AppData\Local\Programs\OpenAI\Codex\bin";
    private const string CodexTarget = @"C:\Users\someone\.codex\packages\standalone\current\bin";
    private const string UserPath = @"%USERPROFILE%\.local\bin;" + Codex + @";C:\Tools";

    public AppDataLinksAppTests()
    {
        Directory.CreateDirectory(_f.Paths.DataRoot);
        _pc.Link(CodexLink, CodexTarget);
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
        _pc.Values.Set(EnvScope.Machine, "Path", @"%SystemRoot%\system32;%SystemRoot%", expand: true);
        _pc.Env["SystemRoot"] = @"C:\Windows";
    }

    public void Dispose() => _f.Dispose();

    private LinkScan Scan() => AppDataLinkFinder.Scan(_pc);

    private AppDataLinkFixes Fixes() => new(_pc, _f.Records, _f.Log);

    private LinkFinding Row(LinkKind kind) => Scan().Findings.Single(f => f.Kind == kind);

    // ------------------------------------------------------------------ what is found

    [Fact]
    public void NothingIsLookedAtWithoutTheClaudeOrCodexApp()
    {
        _pc.Packages.Clear();
        _pc.Packages.Add("Microsoft.WindowsTerminal_1.0.0.0_x64__8wekyb3d8bbwe");
        var scan = Scan();
        Assert.False(scan.Applies);
        Assert.Empty(scan.Findings);
        Assert.Empty(_pc.Looked);
        Assert.Equal(0, _pc.Values.Reads);
        // The Codex Store app counts as the Claude one does.
        _pc.Packages.Add("OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0");
        Assert.True(Scan() is { Applies: true, CodexInstalled: true, ClaudeInstalled: false });
    }

    [Fact]
    public void ALinkedPathEntryIsFoundWithTheFolderItsLinkLeadsTo()
    {
        var scan = Scan();
        var entry = Assert.Single(scan.Findings, f => f.Kind == LinkKind.PathEntry);
        Assert.Equal((EnvScope.User, 1, Codex), (entry.Scope, entry.Index, entry.Entry));
        Assert.Equal(CodexTarget, entry.Target);
        Assert.Equal(Codex, entry.Link);
        Assert.Null(entry.WhyNot);
        Assert.Equal(LinkKind.Projects, scan.Findings[^1].Kind); // the advice about projects, always last
        // The Codex tool's installer made the link: its variable is offered too, so codex update stops putting it back.
        Assert.Equal([LinkFinder.CodexInstall], scan.Findings.Single(f => f.Kind == LinkKind.Codex).EnvSettings.Select(s => s.Name));
        Assert.Equal(2, scan.ToFix);
    }

    [Fact]
    public void AFolderAboveTheEntryThatIsALinkCountsAndARelativeLinkIsReadFromItsFolder()
    {
        _pc.Link(_pc.AppData + @"\Example", @"D:\Example");
        _pc.Real.Add(@"D:\Example\bin\tool.exe");
        _pc.Link(_pc.LocalFolder + @"\Programs\Rel\bin", @"..\..\RelTool\bin", targetThere: false);
        _pc.Real.Add(_pc.LocalFolder + @"\RelTool\bin\tool.exe");
        _pc.Values.Set(EnvScope.User, "Path", @"%APPDATA%\Example\bin;%LOCALAPPDATA%\Programs\Rel\bin", expand: true);
        var rows = Scan().Findings.Where(f => f.Kind == LinkKind.PathEntry).ToList();
        Assert.Equal([@"D:\Example\bin", _pc.LocalFolder + @"\RelTool\bin"], rows.Select(r => r.Target));
        Assert.Equal([@"%APPDATA%\Example", @"%LOCALAPPDATA%\Programs\Rel\bin"], rows.Select(r => r.Link)); // the link itself, where it is
    }

    [Fact]
    public void AnEntryWhoseLinkLeadsNowhereIsToBeRemovedNotReplaced()
    {
        _pc.Links[CodexLink] = @"C:\Users\someone\.codex\gone\bin";
        var gone = Row(LinkKind.PathEntryGone);
        Assert.Null(gone.Target);
        Assert.False(gone.CanFix);
        Assert.Equal((false, "PoolWatch does not replace this entry."), Fixes().Replace(gone, "a test"));
        Assert.Empty(_pc.Values.Writes);
    }

    [Fact]
    public void AFolderReachedThroughAnotherLinkUnderAppDataIsNotWrittenOnThePath()
    {
        _pc.Link(_pc.LocalFolder + @"\Programs\Tool\bin", _pc.LocalFolder + @"\Tool\current\bin", targetThere: false);
        _pc.Link(_pc.LocalFolder + @"\Tool\current", _pc.LocalFolder + @"\Tool\1.2");
        _pc.Real.Add(_pc.LocalFolder + @"\Tool\1.2\bin\tool.exe");
        _pc.Values.Set(EnvScope.User, "Path", @"%LOCALAPPDATA%\Programs\Tool\bin", expand: true);
        var row = Row(LinkKind.PathEntry);
        Assert.Null(row.Target);
        Assert.Equal("the folder it leads to is reached through another link under AppData.", row.WhyNot);
        Assert.False(row.CanFix);
    }

    [Fact]
    public void AnEntryInAPlainValueIsReadAsWrittenAndTheMachinesPathIsLookedAtToo()
    {
        // In a REG_SZ value a %NAME% is not filled in, so that entry is not a path PoolWatch can read.
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: false);
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.PathEntry);
        _pc.Values.Set(EnvScope.Machine, "Path", @"C:\Windows;" + CodexLink);
        var machine = Row(LinkKind.PathEntry);
        Assert.Equal((EnvScope.Machine, 1, CodexLink), (machine.Scope, machine.Index, machine.Entry));
        Assert.Equal(CodexTarget, machine.Target);
    }

    [Fact]
    public void WingetsLinksFolderOnThePathIsNamedWithItsLinks()
    {
        string links = _pc.LocalFolder + @"\Microsoft\WinGet\Links";
        _pc.Link(links + @"\rg.exe", @"C:\Users\someone\AppData\Local\Microsoft\WinGet\Packages\rg\rg.exe", targetThere: false);
        _pc.Link(links + @"\fd.exe", @"C:\Users\someone\AppData\Local\Microsoft\WinGet\Packages\fd\fd.exe", targetThere: false);
        _pc.Values.Set(EnvScope.User, "Path", @"%LOCALAPPDATA%\Microsoft\WinGet\Links", expand: true);
        var row = Row(LinkKind.WinGetLinks);
        Assert.Equal(2, row.Count);
        Assert.False(row.CanFix); // advice only: no change that measured 0 is known for it
    }

    [Fact]
    public void UvsLinksArePythonsToMoveOutOfAppDataUnlessUvAlreadyInstallsElsewhere()
    {
        string python = _pc.AppData + @"\uv\python";
        _pc.Link(python + @"\cpython-3.12-windows-x86_64-none", "cpython-3.12.13-windows-x86_64-none", targetThere: false);
        _pc.Real.Add(python + @"\cpython-3.12.13-windows-x86_64-none\python.exe");
        _pc.Values.Set(EnvScope.User, LinkFinder.UvCache, @"%LOCALAPPDATA%\uv\cache");
        _pc.Env[LinkFinder.UvCache] = _pc.LocalFolder + @"\uv\cache";
        var uv = Row(LinkKind.Uv);
        Assert.Equal((1, false), (uv.Count, uv.Moved));
        Assert.Equal(["cpython-3.12-windows-x86_64-none"], uv.Names);
        Assert.Equal([new EnvSetting(LinkFinder.UvPythons, @"C:\Users\someone\.uv\python", null), new EnvSetting(LinkFinder.UvCache, @"C:\Users\someone\.uv\cache", @"%LOCALAPPDATA%\uv\cache")],
            uv.EnvSettings);

        _pc.Env[LinkFinder.UvPythons] = @"D:\Python\uv";
        uv = Row(LinkKind.Uv);
        Assert.True(uv.Moved);
        Assert.Equal([LinkFinder.UvCache], uv.EnvSettings.Select(s => s.Name));
        _pc.Env[LinkFinder.UvCache] = @"D:\uv-cache";
        Assert.False(Row(LinkKind.Uv).CanFix); // the links are left from before: advice only
    }

    [Fact]
    public void PnpmAndTheCodexToolKeptUnderAppDataAreOfferedTheirVariable()
    {
        _pc.Real.Add(_pc.LocalFolder + @"\pnpm\global\5\node_modules\x");
        var rows = Scan().Findings;
        Assert.Equal([LinkFinder.PnpmHome], rows.Single(f => f.Kind == LinkKind.Pnpm).EnvSettings.Select(s => s.Name));
        Assert.Equal([new EnvSetting(LinkFinder.CodexInstall, @"C:\Users\someone\.codex\bin", null)], rows.Single(f => f.Kind == LinkKind.Codex).EnvSettings);
        _pc.Env[LinkFinder.PnpmHome] = @"D:\pnpm";
        _pc.Env[LinkFinder.CodexInstall] = @"C:\Users\someone\.codex\bin";
        Assert.DoesNotContain(Scan().Findings, f => f.Kind is LinkKind.Pnpm or LinkKind.Codex);
    }

    /// <summary>
    /// A link's target and a PATH entry are text the account can write: any text is read without throwing, and gives a
    /// full path on a local drive, or nothing.
    /// </summary>
    [Fact]
    public void AnyLinkTargetOrPathEntryGivesAFullLocalPathOrNothing()
    {
        var texts = Gen.OneOf(AnyText, Gen.Elements(@"\??\C:\x", @"\\?\C:\x", @"\\?\UNC\s\x", @"\??\Volume{1}\x", @"..\..\x", "x", @"C:\a\..\..\..\b",
            @"%LOCALAPPDATA%\y", @"""C:\q""", "C:", "C:x", "/x", @"\x", @"C:\" + new string('a', 300), @"C:\a;b", "\"\"", " "));
        ForAll(500, texts, t =>
        {
            if (AppDataLinkFinder.Absolute(t, @"C:\Users\someone\AppData\Local\Programs") is { } full) Assert.Matches(@"^[A-Za-z]:\\", full);
            if (AppDataLinkFinder.Expanded(t, expand: true, _pc.Env) is { } entry)
            {
                Assert.True(Core.Settings.IsLocalDrivePath(entry.Length == 2 ? entry + "\\" : entry), entry);
                Assert.DoesNotContain('%', entry);
            }
        });
        Assert.Equal(@"C:\x", AppDataLinkFinder.Absolute(@"\??\C:\x", @"C:\y"));
        Assert.Equal(@"C:\x", AppDataLinkFinder.Absolute(@"\\?\C:\x", @"C:\y"));
        Assert.Null(AppDataLinkFinder.Absolute(@"\\?\UNC\s\x", @"C:\y"));
        Assert.Null(AppDataLinkFinder.Absolute(@"\??\Volume{1}\x", @"C:\y"));
        Assert.Equal(@"C:\a\x", AppDataLinkFinder.Absolute(@"..\x", @"C:\a\b"));
        Assert.Equal(_pc.LocalFolder + @"\y", AppDataLinkFinder.Expanded(@" ""%LOCALAPPDATA%\y\"" ", expand: true, _pc.Env));
    }

    // ------------------------------------------------------------------ replacing a PATH entry

    [Fact]
    public void ReplaceChangesOnlyThatEntryKeepsTheValuesKindAndIsRecordedFirst()
    {
        var row = Row(LinkKind.PathEntry);
        bool recordedFirst = false;
        _pc.Values.BeforeWrite = () => recordedFirst = _f.Records.All().Any(r => r is { Kind: RecordKind.PathEntry, Before: Codex, After: CodexTarget, Index: 1 });

        var (ok, message) = Fixes().Replace(row, "a test");

        Assert.True(ok, message);
        Assert.True(recordedFirst);
        Assert.Equal(new EnvValue(@"%USERPROFILE%\.local\bin;" + CodexTarget + @";C:\Tools", Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.Equal(@"%SystemRoot%\system32;%SystemRoot%", _pc.Values.Text(EnvScope.Machine, "Path")); // the machine's is not touched
        Assert.Equal(["User Path"], _pc.Values.Writes);
        Assert.Equal(1, _pc.Values.Broadcasts);
        Assert.Contains("Quit the Claude and Codex apps fully", message);
        var record = Assert.Single(_f.Records.All());
        Assert.Null(ChangeRecordFile.Invalid(record));
        Assert.Equal(("a test", EnvScopes.User), (record.Via, record.Location));
    }

    [Fact]
    public void APathThatChangedSinceItWasShownIsLeftAsItIs()
    {
        var row = Row(LinkKind.PathEntry);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\New;" + UserPath, expand: true); // an entry put in front since
        Assert.Equal((false, AppDataLinkFixes.ChangedSince), Fixes().Replace(row, "a test"));
        // The link now leads somewhere else.
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
        _pc.Link(CodexLink, @"C:\Users\someone\.codex\packages\standalone\other\bin");
        Assert.Equal((false, AppDataLinkFixes.ChangedSince), Fixes().Replace(row, "a test"));
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());
        Assert.Equal(0, _pc.Values.Broadcasts);
    }

    [Fact]
    public void NothingIsChangedWhenTheChangeCannotBeRecordedAndAFailedWriteTakesItsRecordBack()
    {
        var row = Row(LinkKind.PathEntry);
        string file = Path.Combine(_f.Paths.DataRoot, ChangeRecords.FileName);
        File.WriteAllText(file, "not a list of changes");
        var (ok, message) = Fixes().Replace(row, "a test");
        Assert.False(ok);
        Assert.StartsWith("PoolWatch did not change your PATH, because it could not record the change", message);
        Assert.Empty(_pc.Values.Writes);

        File.Delete(file);
        _pc.Values.WriteError = "Access is denied";
        (ok, message) = Fixes().Replace(row, "a test");
        Assert.False(ok);
        Assert.Equal("PoolWatch could not change your PATH: Access is denied.", message);
        Assert.Empty(_f.Records.All()); // taken back
        Assert.Equal(UserPath, _pc.Values.Text(EnvScope.User, "Path"));
    }

    [Fact]
    public void PutBackPutsTheEntryBackWhereverTheFolderIsNowOrLeavesAPathThatNoLongerHoldsIt()
    {
        Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
        var source = new RecordedSource(_f.Records, _f.Toggles, _f.ClearTasks, _f.Live(), environment: _pc.Store);
        var row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        Assert.Equal(ChangeGroup.Other, row.Group);
        Assert.Equal(ChangeWords.PutBack, ActiveChange.ButtonOf(row));

        // codex update put its link in front since: PoolWatch's folder is found one place on, and only it goes back.
        string updated = Codex + ";" + _pc.Values.Text(EnvScope.User, "Path");
        _pc.Values.Set(EnvScope.User, "Path", updated, expand: true);
        // ... and the tray tells about the entry that is back, which can be replaced again.
        var back = AppDataLinkFinder.Back(Scan(), _f.Records.All());
        Assert.Equal([(0, Codex)], back.Select(f => (f.Index, f.Entry)));

        var result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.Equal(new EnvValue(Codex + ";" + UserPath, Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.Empty(_f.Records.All());

        // A PATH that no longer holds the folder: the record goes, and the PATH is left as it is.
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
        Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
        row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\Tools", expand: true);
        int writes = _pc.Values.Writes.Count;
        result = source.Undo(row, confirmed: false);
        Assert.True(result.Done);
        Assert.Contains("nothing to put back", result.Message);
        Assert.Equal(writes, _pc.Values.Writes.Count);
        Assert.Equal(@"C:\Tools", _pc.Values.Text(EnvScope.User, "Path"));
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void WithNoStoreGivenNothingIsPutBack()
    {
        Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
        var source = _f.Recorded(_f.Live());
        var result = source.Undo(source.List().Single(c => c.Kind == ChangeKind.PathEntry), confirmed: false);
        Assert.False(result.Done);
        Assert.Single(_f.Records.All());
    }

    // ------------------------------------------------------------------ the guided fix

    [Fact]
    public void TheVariablesAreSetAsShownRecordedAndPutBack()
    {
        string python = _pc.AppData + @"\uv\python";
        _pc.Link(python + @"\cpython-3.13-windows-x86_64-none", python + @"\cpython-3.13.15-windows-x86_64-none");
        _pc.Values.Set(EnvScope.User, LinkFinder.UvCache, @"%LOCALAPPDATA%\uv\cache", expand: true);
        var uv = Row(LinkKind.Uv);

        var (ok, said, set) = Fixes().SetVariables(uv, "a test");

        Assert.True(ok, said);
        Assert.Equal(uv.EnvSettings, set);
        Assert.Equal(new EnvValue(@"C:\Users\someone\.uv\python", Expand: false), _pc.Values.Read(EnvScope.User, LinkFinder.UvPythons));
        Assert.Equal(new EnvValue(@"C:\Users\someone\.uv\cache", Expand: false), _pc.Values.Read(EnvScope.User, LinkFinder.UvCache));
        Assert.Equal(1, _pc.Values.Broadcasts);
        var records = _f.Records.All();
        Assert.Equal([(LinkFinder.UvCache, @"%LOCALAPPDATA%\uv\cache", "ExpandString"), (LinkFinder.UvPythons, (string?)null, (string?)null)],
            records.Select(r => (r.Name!, r.Before, r.ValueKind)).Order());
        Assert.All(records, r => Assert.Null(ChangeRecordFile.Invalid(r)));

        // Put back: a variable that was not set is removed, one that was is set back with its kind; one changed since stays.
        var source = new RecordedSource(_f.Records, _f.Toggles, _f.ClearTasks, _f.Live(), environment: _pc.Store);
        var rows = source.List().Where(c => c.Kind == ChangeKind.EnvironmentVariable).ToList();
        Assert.Equal(2, rows.Count);
        _pc.Values.Set(EnvScope.User, LinkFinder.UvPythons, @"D:\mine");
        foreach (var row in rows) Assert.True(source.Undo(row, confirmed: false).Done);
        Assert.Equal(@"D:\mine", _pc.Values.Text(EnvScope.User, LinkFinder.UvPythons));
        Assert.Equal(new EnvValue(@"%LOCALAPPDATA%\uv\cache", Expand: true), _pc.Values.Read(EnvScope.User, LinkFinder.UvCache));
        Assert.Empty(_f.Records.All());

        // Not set before: removed.
        _pc.Values.Values.Remove((EnvScope.User, LinkFinder.UvPythons));
        _pc.Values.Values.Remove((EnvScope.User, LinkFinder.UvCache));
        Assert.True(Fixes().SetVariables(Row(LinkKind.Uv), "a test").Ok);
        foreach (var row in source.List().Where(c => c.Kind == ChangeKind.EnvironmentVariable)) Assert.True(source.Undo(row, confirmed: false).Done);
        Assert.Null(_pc.Values.Read(EnvScope.User, LinkFinder.UvPythons));
        Assert.Null(_pc.Values.Read(EnvScope.User, LinkFinder.UvCache));
    }

    [Fact]
    public void AVariableThatChangedSinceItWasShownIsLeftAndTheOthersAreSet()
    {
        _pc.Link(_pc.AppData + @"\uv\python\cpython-3.13-windows-x86_64-none", _pc.AppData + @"\uv\python\cpython-3.13.15-windows-x86_64-none");
        var uv = Row(LinkKind.Uv);
        _pc.Values.Set(EnvScope.User, LinkFinder.UvCache, @"D:\cache");
        var (ok, said, set) = Fixes().SetVariables(uv, "a test");
        Assert.False(ok);
        Assert.Equal([LinkFinder.UvPythons], set.Select(s => s.Name));
        Assert.Contains("UV_CACHE_DIR was not set: it changed since PoolWatch looked at it.", said);
        Assert.Equal(@"D:\cache", _pc.Values.Text(EnvScope.User, LinkFinder.UvCache));
        Assert.Equal([LinkFinder.UvPythons], _f.Records.All().Select(r => r.Name));
    }

    [Fact]
    public void AVariableIsSetOnlyToItsOwnFolderWithNoLinkOnTheWay()
    {
        var fixes = Fixes();
        var mine = new EnvSetting(LinkFinder.UvPythons, @"C:\Users\someone\.uv\python", null);
        Assert.Null(fixes.WhyNotSet(mine));
        Assert.Equal("PoolWatch does not set that variable", fixes.WhyNotSet(mine with { Name = "ComSpec" }));
        Assert.Equal("that is not the folder PoolWatch sets it to", fixes.WhyNotSet(mine with { Value = @"C:\Users\someone\AppData\Local\uv" }));
        Assert.Equal("that is not the folder PoolWatch sets it to", fixes.WhyNotSet(mine with { Value = @"D:\elsewhere" }));
        _pc.Link(_pc.Profile + @"\.uv", @"D:\elsewhere");
        Assert.Equal(@"C:\Users\someone\.uv is a link to another place", fixes.WhyNotSet(mine));
        var row = new LinkFinding { Kind = LinkKind.Uv, EnvSettings = [mine] };
        var (ok, said, set) = fixes.SetVariables(row, "a test");
        Assert.False(ok);
        Assert.Empty(set);
        Assert.Contains("is a link to another place", said);
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());
    }

    /// <summary>
    /// The real look on a junction of the test's own, in its TEMP folder (nothing outside it is opened or changed): the
    /// junction is a link whose target is read through its own handle, what is through it is followed, and what is not
    /// there is not there.
    /// </summary>
    [Fact]
    public void TheRealLookReadsAJunctionsTargetThroughItsOwnHandle()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-links-" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(root, "target"), link = Path.Combine(root, "link");
            Directory.CreateDirectory(Path.Combine(target, "bin"));
            Links.Junction(link, target);
            var pc = new WindowsLinkSystem();
            var look = pc.Look(link);
            Assert.Equal(LinkState.Link, look.State);
            string? leads = AppDataLinkFinder.Absolute(look.Target, root);
            Assert.NotNull(leads);
            Assert.True(NoFollow.SamePath(NoFollow.ResolvedPath(leads, dir: true), NoFollow.ResolvedPath(target, dir: true)!), $"{look.Target} is not {target}");
            Assert.Equal(LinkState.Plain, pc.Look(target).State);
            Assert.Equal(LinkState.Plain, pc.Look(Path.Combine(link, "bin")).State); // the folder in it, through the link
            Assert.Equal(LinkState.NotThere, pc.Look(Path.Combine(root, "none")).State);
            Assert.Equal(LinkState.NotThere, pc.Look(Path.Combine(root, "none", "deeper")).State);
            Assert.True(NoFollow.SamePath(pc.Resolved(link), NoFollow.ResolvedPath(target, dir: true)!));
            Assert.Null(pc.Resolved(Path.Combine(root, "none")));
            Assert.Equal(["bin"], pc.Folders(link, 10).Select(f => Path.GetFileName(f)));
            Assert.Equal(1, pc.LinksIn(root, 10));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ the registry key, the watch, the window

    [Fact]
    public void OnlyTheAccountsOwnEnvironmentKeyAndTheMachinesAreUsed()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        Assert.Null(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, $@"\REGISTRY\USER\{sid}\Environment", sid));
        Assert.Null(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, $@"\Registry\User\{sid}\ENVIRONMENT", sid));
        // A link in the account's key that leads to another account's key, or anywhere else: refused.
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, @"\REGISTRY\USER\S-1-5-21-1-2-3-500\Environment", sid));
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Control\Session Manager\Environment", sid));
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, null, sid));
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.User, $@"\REGISTRY\USER\{sid}\Environment", null));
        Assert.Null(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.Machine, @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Control\Session Manager\Environment", sid));
        Assert.Null(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.Machine, @"\REGISTRY\MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", sid));
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.Machine, $@"\REGISTRY\USER\{sid}\Environment", sid));
        Assert.NotNull(WindowsEnvironmentStore.WhyNotItsKey(EnvScope.Machine, @"\REGISTRY\MACHINE\SOFTWARE\Example\Environment", sid));
    }

    [Fact]
    public async Task TheWatchLooksAtMostOnceAnHourByItselfAndAtOnceWhenAsked()
    {
        long now = 0;
        int looks = 0;
        var watch = new AppDataLinkWatch(() => { looks++; return Scan(); }, _f.Log, () => now);
        Assert.Null(watch.Last);
        await watch.ScanIfOld()!;
        Assert.Null(watch.ScanIfOld());
        now += (long)TimeSpan.FromMinutes(59).TotalMilliseconds;
        Assert.Null(watch.ScanIfOld());
        await watch.ScanNow();
        Assert.Equal(2, looks);
        now += (long)AppDataLinkWatch.Every.TotalMilliseconds;
        await watch.ScanIfOld()!;
        Assert.Equal(3, looks);
        Assert.Equal(2, watch.Last!.ToFix);
    }

    private sealed class Answers(bool yes) : IAppDataLinkDialogs
    {
        public readonly List<string> Asked = new();
        public readonly List<(string Steps, string Prompt)> Shown = new();
        public bool Confirm(string title, string question) { Asked.Add(question); return yes; }
        public void ShowSteps(string title, string steps, string prompt) => Shown.Add((steps, prompt));
    }

    private AppDataLinksView View(bool smoke = false, List<string>? timeline = null) =>
        new(new AppDataLinkWatch(Scan, _f.Log), Fixes(), smoke, line => timeline?.Add(line), _pc.Profile);

    [Fact]
    public async Task TheWindowChangesNothingWithoutYourYesAndNothingInTheTestRun()
    {
        var row = Row(LinkKind.PathEntry);
        var no = new Answers(false);
        Assert.Equal(AppDataLinksView.NothingChanged, await View().Fix(row, no));
        Assert.Single(no.Asked);
        Assert.Contains(CodexTarget, no.Asked[0]);
        var yes = new Answers(true);
        Assert.Equal(RecommendationActions.Refused, await View(smoke: true).Fix(row, yes));
        Assert.Empty(yes.Asked);
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());

        var timeline = new List<string>();
        Assert.StartsWith("Replaced " + Codex, await View(timeline: timeline).Fix(row, yes));
        Assert.Single(timeline);
        Assert.Equal(CodexTarget, _pc.Values.Text(EnvScope.User, "Path")!.Split(';')[1]);
    }

    [Fact]
    public async Task TheGuidedFixShowsTheStepsLeftWithAPromptThatNamesNoUser()
    {
        _pc.Real.Add(_pc.LocalFolder + @"\pnpm\global\5\node_modules\x");
        var pnpm = Row(LinkKind.Pnpm);
        var yes = new Answers(true);
        string said = await View().Fix(pnpm, yes);
        Assert.Equal(@"Set PNPM_HOME to C:\Users\someone\.pnpm.", said);
        var (steps, prompt) = Assert.Single(yes.Shown);
        Assert.Contains(@"PNPM_HOME = C:\Users\someone\.pnpm", steps);
        Assert.Contains(@"PNPM_HOME = %USERPROFILE%\.pnpm", prompt);
        Assert.DoesNotContain("someone", prompt);
    }

    [Fact]
    public void TheStatusLineSaysWhatWasFound()
    {
        Assert.Equal(LinkWords.NotInstalled, AppDataLinksView.Status(LinkScan.None(DateTime.UtcNow)));
        Assert.Equal(LinkWords.NothingFound, AppDataLinksView.Status(new LinkScan(DateTime.UtcNow, true, false, [new LinkFinding { Kind = LinkKind.Projects }])));
        Assert.StartsWith("PoolWatch found 2 things that go through links under AppData; PoolWatch can fix 2 of them.", AppDataLinksView.Status(Scan()));
    }

    /// <summary>The running app and the command line (the uninstaller's put-back) both put the changes back through the registry.</summary>
    [Fact]
    public void TheAppAndTheUninstallerPutTheChangesBackThroughTheRegistry()
    {
        string ledger = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "Changes", "ChangeLedger.cs"));
        Assert.Contains("new RecordedSource(records, new TasksAndStartup(engine.Log), new WindowsClearTaskStore(), live, environment: WindowsEnvironmentStore.Instance),", ledger);
        Assert.Contains("new RecordedSource(records, new TasksAndStartup(log), new WindowsClearTaskStore(), live, environment: WindowsEnvironmentStore.Instance),", ledger);
    }
}
