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
    /// <summary>Drive letters ("E:") that are not there for PoolWatch: one mapped or subst'ed only in your own sign-in, or not connected now.</summary>
    public HashSet<string> AbsentDrives { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders PoolWatch may not open: a look at one throws, as Windows' "access is denied" does, and nothing through one resolves.</summary>
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        if (Resolved(p[..cut]) is not { } parent) return new(AtPath.NotThere);
        string at = parent + p[cut..];
        if (Denied.Contains(at)) throw new IOException(at + ": Access is denied.");
        if (Links.TryGetValue(at, out var target)) return new(AtPath.Link, target);
        return Exists(at) ? new(AtPath.Plain) : new(AtPath.NotThere);
    }

    public string? Resolved(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        if (AbsentDrives.Contains(parts[0])) return null;
        string at = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            at += "\\" + parts[i];
            if (Denied.Contains(at)) return null;
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

    /// <summary>The text of the small files a look reads, by their real paths; each is a real file too.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ReadPaths { get; } = new();

    public void AddFile(string path, string text)
    {
        Files[path] = text;
        Real.Add(path);
    }

    /// <summary>Like Windows' read: a link at the path itself is never read, and a longer file is not read at all.</summary>
    public string? ReadText(string path, int maxBytes)
    {
        ReadPaths.Add(path);
        if (Links.ContainsKey(path) || Resolved(path) is not { } at || !Files.TryGetValue(at, out var text)) return null;
        return System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes ? null : text;
    }

    public (bool? FastStartup, int? BootType) BootSeen { get; set; }
    public (bool? FastStartup, int? BootType) Boot() => BootSeen;

    public List<AppProcess> Running { get; } = new();
    public IReadOnlyList<AppProcess> AppProcesses() => Running;
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

    /// <summary>
    /// An entry whose link leads nowhere (an uninstall left it) is removed from your own PATH on your yes, never replaced:
    /// recorded first with the SHA-256 of the PATH it leaves, every other entry and the value's kind kept, the link itself
    /// left alone. Put back gives the PATH back exactly while it is still as PoolWatch left it (on a PATH that changed
    /// since: <see cref="ARemovedEntryGoesBackAfterThePathChangedAndTheSummaryNeverSaysPutBackForWhatWasNotWritten"/>).
    /// </summary>
    [Fact]
    public void AnEntryWhoseLinkLeadsNowhereIsRemovedNotReplacedAndPutBackExactly()
    {
        _pc.Links[CodexLink] = @"C:\Users\someone\.codex\gone\bin";
        var gone = Row(LinkKind.PathEntryGone);
        Assert.Equal(@"C:\Users\someone\.codex\gone\bin", gone.Target); // the folder it leads to, which is not there
        Assert.True(gone.CanFix);
        Assert.Equal((false, "PoolWatch does not replace this entry."), Fixes().Replace(gone, "a test"));
        Assert.Empty(_pc.Values.Writes);

        bool recordedFirst = false;
        _pc.Values.BeforeWrite = () => recordedFirst = _f.Records.All().Any(r => r is { Kind: RecordKind.RemovedPathEntry, Before: Codex, Index: 1 });
        var (ok, message) = Fixes().Remove(gone, "a test");
        Assert.True(ok, message);
        Assert.True(recordedFirst);
        Assert.StartsWith("Removed " + Codex + " from your PATH", message);
        const string left = @"%USERPROFILE%\.local\bin;C:\Tools";
        Assert.Equal(new EnvValue(left, Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.Equal(1, _pc.Values.Broadcasts);
        Assert.True(_pc.Links.ContainsKey(CodexLink)); // the link itself is never deleted
        var record = Assert.Single(_f.Records.All());
        Assert.Null(ChangeRecordFile.Invalid(record));
        Assert.Equal(PathList.Hash(left), record.Sha256);
        _pc.Values.BeforeWrite = null;

        // Put back: the PATH is still as PoolWatch left it, so it comes back exactly, its kind kept.
        var source = new RecordedSource(_f.Records, _f.Toggles, _f.ClearTasks, _f.Live(), environment: _pc.Store);
        var row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        Assert.Equal(ChangeGroup.Other, row.Group);
        var result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.Null(result.Caveat);
        Assert.Equal(new EnvValue(UserPath, Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void AnEntryIsNeverRemovedFromTheMachinesPathOrWhenItIsTheOnlyOne()
    {
        _pc.Links[CodexLink] = @"C:\Users\someone\.codex\gone\bin";
        _pc.Values.Set(EnvScope.User, "Path", @"C:\Tools", expand: true);
        _pc.Values.Set(EnvScope.Machine, "Path", @"C:\Windows;" + CodexLink);
        var machine = Row(LinkKind.PathEntryGone);
        Assert.Equal(EnvScope.Machine, machine.Scope);
        Assert.False(machine.CanFix);
        Assert.Equal((false, "PoolWatch does not remove this entry."), Fixes().Remove(machine, "a test"));

        _pc.Values.Set(EnvScope.Machine, "Path", @"C:\Windows");
        _pc.Values.Set(EnvScope.User, "Path", Codex, expand: true);
        var (ok, message) = Fixes().Remove(Row(LinkKind.PathEntryGone), "a test");
        Assert.False(ok);
        Assert.Contains("it is the only one on your PATH", message);
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());
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
        // Shown, as advice only: PoolWatch never writes the machine's PATH.
        Assert.Null(machine.Target);
        Assert.Equal(LinkWords.MachinePath, machine.WhyNot);
        Assert.False(machine.CanFix);
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

        // codex update put its link in front since: PoolWatch's folder is found one place on.
        string updated = Codex + ";" + _pc.Values.Text(EnvScope.User, "Path");
        _pc.Values.Set(EnvScope.User, "Path", updated, expand: true);
        // ... and the tray tells about the entry that is back, which can be fixed again.
        var back = AppDataLinkFinder.Back(Scan(), _f.Records.All());
        Assert.Equal([(0, Codex)], back.Select(f => (f.Index, f.Entry)));

        // The entry is on the PATH again already: PoolWatch's folder is taken out, so the entry is there once, and Put back
        // says it did not write the entry a second time.
        var result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.NotNull(result.Caveat);
        Assert.Equal(new EnvValue(Codex + @";%USERPROFILE%\.local\bin;C:\Tools", Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.Empty(_f.Records.All());

        // A PATH that no longer holds the folder: the record goes, and the PATH is left as it is.
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
        Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
        row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\Tools", expand: true);
        int writes = _pc.Values.Writes.Count;
        result = source.Undo(row, confirmed: false);
        Assert.True(result.Done);
        Assert.Contains("so PoolWatch wrote nothing", result.Message);
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
            Assert.Equal(AtPath.Link, look.State);
            string? leads = AppDataLinkFinder.Absolute(look.Target, root);
            Assert.NotNull(leads);
            Assert.True(NoFollow.SamePath(NoFollow.ResolvedPath(leads, dir: true), NoFollow.ResolvedPath(target, dir: true)!), $"{look.Target} is not {target}");
            Assert.Equal(AtPath.Plain, pc.Look(target).State);
            Assert.Equal(AtPath.Plain, pc.Look(Path.Combine(link, "bin")).State); // the folder in it, through the link
            Assert.Equal(AtPath.NotThere, pc.Look(Path.Combine(root, "none")).State);
            Assert.Equal(AtPath.NotThere, pc.Look(Path.Combine(root, "none", "deeper")).State);
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

    // ------------------------------------------------------------------ Claude Code's temp folder, the apps' data folders, their own copies

    /// <summary>
    /// Claude Code's temp folder as a junction (anthropics/claude-code issue 95489) is offered CLAUDE_CODE_TMPDIR, through
    /// the same guided fix as uv's variables: recorded first, and put back from Active changes. Only with the Claude app,
    /// and not once the variable is set outside AppData.
    /// </summary>
    [Fact]
    public void ClaudeCodesTempFolderAsAJunctionIsOfferedItsVariable()
    {
        _pc.Link(_pc.LocalFolder + @"\Temp\claude", @"D:\ClaudeScratch");
        var row = Row(LinkKind.ClaudeTemp);
        Assert.Equal((@"%LOCALAPPDATA%\Temp\claude", @"D:\ClaudeScratch"), (row.Link, row.Target));
        Assert.Equal([new EnvSetting(LinkFinder.ClaudeTemp, @"C:\Users\someone\.claude-tmp", null)], row.EnvSettings);
        Assert.True(row.CanFix);
        var (ok, said, _) = Fixes().SetVariables(row, "a test");
        Assert.True(ok, said);
        Assert.Equal(new EnvValue(@"C:\Users\someone\.claude-tmp", Expand: false), _pc.Values.Read(EnvScope.User, LinkFinder.ClaudeTemp));
        Assert.Null(ChangeRecordFile.Invalid(Assert.Single(_f.Records.All())));

        _pc.Env[LinkFinder.ClaudeTemp] = @"C:\Users\someone\.claude-tmp"; // set outside AppData: Claude Code no longer uses the junction
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.ClaudeTemp);
        _pc.Env.Remove(LinkFinder.ClaudeTemp);
        _pc.Packages.Clear();
        _pc.Packages.Add("OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0"); // the Codex app has no Claude Code
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.ClaudeTemp);
    }

    [Fact]
    public async Task AnAppsDataFolderAsAJunctionGetsStepsAndAPromptAndNothingIsChanged()
    {
        _pc.Link(_pc.AppData + @"\Claude", @"E:\CLAUDE");
        var row = Row(LinkKind.AppFolder);
        Assert.Equal((LinkApps.Claude, @"%APPDATA%\Claude", @"E:\CLAUDE"), (row.Tool, row.Link, row.Target));
        Assert.True(row is { HasSteps: true, CanFix: false, InApp: null });
        // The Codex app's data folders are looked at only with the Codex app.
        _pc.Link(_pc.LocalFolder + @"\OpenAI\Codex", @"E:\Codex");
        Assert.DoesNotContain(Scan().Findings, f => f is { Kind: LinkKind.AppFolder, Tool: LinkApps.Codex });
        _pc.Packages.Add("OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0");
        Assert.Contains(Scan().Findings, f => f is { Kind: LinkKind.AppFolder, Tool: LinkApps.Codex, Link: @"%LOCALAPPDATA%\OpenAI\Codex" });

        var yes = new Answers(true);
        Assert.Equal("", await View().Fix(row, yes));
        Assert.Empty(yes.Asked); // nothing changes, so nothing is asked
        var (steps, prompt) = Assert.Single(yes.Shown);
        Assert.Contains("1. Quit the app fully", steps);
        Assert.Contains(@"%APPDATA%\Claude", prompt);
        Assert.DoesNotContain("someone", prompt);
        Assert.Equal(RecommendationActions.Refused, await View(smoke: true).Fix(row, yes));
        Assert.Single(yes.Shown);
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());
    }

    /// <summary>
    /// What a program the app started made in the app's own copy of AppData (its package's LocalCache) is found too, named
    /// as being there, and only advised: PoolWatch changes nothing there. A package name that is not a plain one is never
    /// made into a folder to look in.
    /// </summary>
    [Fact]
    public void TheAppsOwnCopyOfAppDataIsLookedAtTooAndOnlyAdvised()
    {
        string cache = _pc.LocalFolder + @"\Packages\Claude_pzs8sxrjxfjjc\LocalCache";
        _pc.Link(cache + @"\Roaming\uv\python\cpython-3.12-windows-x86_64-none", "cpython-3.12.13-windows-x86_64-none", targetThere: false);
        _pc.Real.Add(cache + @"\Roaming\uv\python\cpython-3.12.13-windows-x86_64-none\python.exe");
        var row = Assert.Single(Scan().Findings, f => f.InApp is not null);
        Assert.Equal((LinkKind.Uv, LinkApps.Claude, 1), (row.Kind, row.InApp, row.Count));
        Assert.Equal(@"%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\uv\python", row.Link);
        Assert.False(row.Actionable);
        Assert.Equal("Advice only", LinkWords.Fix(row));
        var (ok, _, set) = Fixes().SetVariables(row with { EnvSettings = [new(LinkFinder.UvPythons, @"C:\Users\someone\.uv\python", null)] }, "a test");
        Assert.False(ok);
        Assert.Empty(set);
        Assert.Empty(_pc.Values.Writes);

        _pc.Packages.Clear();
        _pc.Packages.Add(@"Claude_1.0.0.0_x64__..\..\..\x");
        Assert.DoesNotContain(Scan().Findings, f => f.InApp is not null);
        Assert.DoesNotContain(_pc.Looked, p => p.Contains(@"\..\", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ venvs on PATH, and Claude Code's hooks

    /// <summary>
    /// PlatformIO's venv is a plain folder on PATH, but its Python (home in pyvenv.cfg) is behind uv's link, so every python
    /// a program of the app runs goes through it (SynoSoftware). It is found from the pyvenv.cfg beside the Scripts folder,
    /// and only advised.
    /// </summary>
    [Fact]
    public void AVenvOnThePathWhosePythonIsBehindUvsLinkIsFound()
    {
        string python = _pc.AppData + @"\uv\python";
        _pc.Link(python + @"\cpython-3.13-windows-x86_64-none", python + @"\cpython-3.13.5-windows-x86_64-none");
        string penv = _pc.Profile + @"\.platformio\penv";
        _pc.AddFile(penv + @"\pyvenv.cfg", "home = " + python + "\\cpython-3.13-windows-x86_64-none\r\ninclude-system-site-packages = false\r\n");
        _pc.Values.Set(EnvScope.User, "Path", penv + @"\Scripts;" + UserPath, expand: true);
        var venv = Row(LinkKind.Venv);
        Assert.Equal(("PlatformIO", 0, @"%USERPROFILE%\.platformio\penv", @"%APPDATA%\uv\python\cpython-3.13-windows-x86_64-none"),
            (venv.Tool, venv.Index, venv.Link, venv.Home));
        Assert.True(venv is { HasSteps: true, CanFix: false });
        Assert.Contains(penv + @"\pyvenv.cfg", _pc.ReadPaths);
        // A venv on a Python in a real folder goes through no link.
        _pc.AddFile(penv + @"\pyvenv.cfg", "home = " + python + "\\cpython-3.13.5-windows-x86_64-none\r\n");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.Venv);
    }

    [Fact]
    public void HooksThatStartPythonAreCountedWhileUvsPythonsAreBehindItsLinks()
    {
        _pc.AddFile(_pc.Profile + @"\.claude\settings.json",
            """{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "python3 check.py" }, { "type": "command", "command": "node log.js" } ] } ] } }""");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.Hook); // uv's Pythons are not behind links here
        _pc.Link(_pc.AppData + @"\uv\python\cpython-3.12-windows-x86_64-none", "cpython-3.12.13-windows-x86_64-none", targetThere: false);
        _pc.Real.Add(_pc.AppData + @"\uv\python\cpython-3.12.13-windows-x86_64-none\python.exe");
        var hook = Row(LinkKind.Hook);
        Assert.Equal(1, hook.Count);
        Assert.Equal(["python3 check.py"], hook.Names);
        Assert.True(hook is { HasSteps: true, CanFix: false });
        Assert.DoesNotContain("check.py", LinkWords.GuidePrompt(hook, _pc.Profile)); // a command never leaves the window
    }

    // ------------------------------------------------------------------ came back, the hints, the apps' programs

    [Fact]
    public void AVariableAToolPutBackIntoAppDataIsToldAboutAgain()
    {
        _pc.Real.Add(_pc.LocalFolder + @"\pnpm\global\5\node_modules\x");
        Assert.True(Fixes().SetVariables(Row(LinkKind.Pnpm), "a test").Ok);
        _pc.Env[LinkFinder.PnpmHome] = @"C:\Users\someone\.pnpm";
        Assert.Empty(AppDataLinkFinder.Back(Scan(), _f.Records.All()));
        _pc.Env[LinkFinder.PnpmHome] = _pc.LocalFolder + @"\pnpm"; // an installer set it back into AppData
        var back = AppDataLinkFinder.Back(Scan(), _f.Records.All());
        Assert.Equal([LinkKind.Pnpm], back.Select(f => f.Kind));
        Assert.StartsWith("Back under AppData: pnpm's global packages.", LinkWords.CameBack(back));
    }

    [Fact]
    public async Task TheWindowSaysWhatTheBlocksTheLastStartAndTheAppsProgramsShowAndEndsNothing()
    {
        _pc.BootSeen = (true, 1);
        var view = View();
        var entry = new Core.Model.PoolTagEntry(Core.PoolTag.Parse("NtFC"), 0, 0, 0, 2_000_100, 100, 352UL * 2_000_000);
        var scan = new LinkScan(DateTime.UtcNow, true, false,
            [new LinkFinding { Kind = LinkKind.Uv, Link = @"%APPDATA%\uv\python", Count = 1, Names = ["cpython-3.14-windows-x86_64-none"], Block = 352 }]);
        string hints = view.Hints(scan, entry)!;
        Assert.Contains("NTFS records (NtFC) average 352 bytes each since Windows started, the size an open through uv's Pythons (1 link) keeps", hints);
        Assert.Contains("Windows last started from a Shut down with Fast Startup on", hints);
        Assert.Equal("Fast Startup is on: to free what leaked, use Restart, not Shut down (DraftingDreamer).", view.Hints(scan, entry with { NonPagedUsed = 1 << 20 }));
        _pc.BootSeen = (false, 0);
        Assert.Null(view.Hints(scan, null));

        // After a change, the apps' programs that started before it are named; none is ended.
        _pc.Running.Add(new AppProcess("claude.exe", LinkApps.Claude, DateTime.UtcNow.AddHours(-1)));
        _pc.Running.Add(new AppProcess("cowork-svc.exe", LinkApps.Claude, DateTime.UtcNow.AddHours(-1)));
        string said = await View().Fix(Row(LinkKind.PathEntry), new Answers(true));
        Assert.StartsWith("Replaced " + Codex, said);
        Assert.Contains("2 programs of the Claude app (claude.exe, cowork-svc.exe) started before PoolWatch's change", said);
        Assert.Contains("PoolWatch never ends them", view.Hints(scan, null));
        Assert.Equal(2, _pc.Running.Count);
    }

    /// <summary>The running app and the command line (the uninstaller's put-back) both put the changes back through the registry.</summary>
    [Fact]
    public void TheAppAndTheUninstallerPutTheChangesBackThroughTheRegistry()
    {
        string ledger = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "Changes", "ChangeLedger.cs"));
        Assert.Contains("new RecordedSource(records, new TasksAndStartup(engine.Log), new WindowsClearTaskStore(), live, environment: WindowsEnvironmentStore.Instance),", ledger);
        Assert.Contains("new RecordedSource(records, new TasksAndStartup(log), new WindowsClearTaskStore(), live, environment: WindowsEnvironmentStore.Instance),", ledger);
    }

    // ------------------------------------------------------------------ the review of this round: the machine's PATH, put back, gone, twice

    private RecordedSource Source() => new(_f.Records, _f.Toggles, _f.ClearTasks, _f.Live(), environment: _pc.Store);

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    /// <summary>
    /// The machine's PATH, which every account and Windows' own services read, is advice only: an entry there through a link
    /// under AppData is shown, and PoolWatch never writes the machine's PATH, whatever row it is given.
    /// </summary>
    [Fact]
    public void TheMachinesPathIsAdviceOnlyAndNeverWritten()
    {
        _pc.Values.Set(EnvScope.User, "Path", @"C:\Tools", expand: true);
        _pc.Values.Set(EnvScope.Machine, "Path", @"%SystemRoot%\system32;" + Codex, expand: true);
        var row = Row(LinkKind.PathEntry);
        Assert.Equal((EnvScope.Machine, 1, Codex), (row.Scope, row.Index, row.Entry));
        Assert.False(row.CanFix);
        Assert.False((row with { Target = CodexTarget }).CanFix);
        Assert.Equal("Change it yourself", LinkWords.Fix(row));
        Assert.Equal((false, "PoolWatch does not replace this entry."), Fixes().Replace(row with { Target = CodexTarget }, "a test"));
        Assert.Equal((false, "PoolWatch does not replace this entry."), Fixes().Replace(row, "a test"));
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());
        Assert.Equal(@"%SystemRoot%\system32;" + Codex, _pc.Values.Text(EnvScope.Machine, "Path"));
    }

    /// <summary>
    /// A removed entry is never lost: when your PATH changed after PoolWatch removed it, Put back puts it back at its old
    /// place among the entries there now, or, when it is on your PATH again already, writes nothing. The uninstaller's
    /// summary then says the change was put back only in part, never a plain "Put back:" for an entry PoolWatch did not
    /// write. The same for a replaced entry whose folder is no longer on the PATH.
    /// </summary>
    [Fact]
    public void ARemovedEntryGoesBackAfterThePathChangedAndTheSummaryNeverSaysPutBackForWhatWasNotWritten()
    {
        _pc.Links[CodexLink] = @"C:\Users\someone\.codex\gone\bin";
        const string left = @"%USERPROFILE%\.local\bin;C:\Tools";
        var source = Source();

        Assert.True(Fixes().Remove(Row(LinkKind.PathEntryGone), "a test").Ok);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\New;" + left, expand: true); // an installer put an entry in front since
        var row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        var result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.Equal(new EnvValue(@"C:\New;" + Codex + ";" + left, Expand: true), _pc.Values.Read(EnvScope.User, "Path"));
        Assert.NotNull(result.Caveat);
        Assert.Empty(_f.Records.All());
        var lines = Lines(Program.UninstallSummary([row], null, [(row, result)], DateTime.Now));
        Assert.Contains(lines, l => l.StartsWith("Partly put back: " + row.What, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Put back: ", StringComparison.Ordinal));

        // On your PATH again already (the tool's installer put it back): nothing is written, and it is not "Put back" either.
        Assert.True(Fixes().Remove(Row(LinkKind.PathEntryGone), "a test").Ok);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\New;" + left + ";" + Codex, expand: true);
        int writes = _pc.Values.Writes.Count;
        row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.NotNull(result.Caveat);
        Assert.Equal(writes, _pc.Values.Writes.Count);
        Assert.Empty(_f.Records.All());
        Assert.DoesNotContain(Lines(Program.UninstallSummary([row], null, [(row, result)], DateTime.Now)), l => l.StartsWith("Put back: ", StringComparison.Ordinal));

        // A replaced entry whose folder is no longer on the PATH: nothing is written, and the summary does not say "Put back".
        _pc.Links[CodexLink] = CodexTarget;
        _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
        Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
        _pc.Values.Set(EnvScope.User, "Path", @"C:\Tools", expand: true);
        writes = _pc.Values.Writes.Count;
        row = source.List().Single(c => c.Kind == ChangeKind.PathEntry);
        result = source.Undo(row, confirmed: false);
        Assert.True(result.Done, result.Message);
        Assert.NotNull(result.Caveat);
        Assert.Equal(writes, _pc.Values.Writes.Count);
        Assert.Equal(@"C:\Tools", _pc.Values.Text(EnvScope.User, "Path"));
        Assert.DoesNotContain(Lines(Program.UninstallSummary([row], null, [(row, result)], DateTime.Now)), l => l.StartsWith("Put back: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Leads nowhere" is judged as PoolWatch sees it, as administrator. A drive it does not see (one mapped or subst'ed only
    /// in your own sign-in, or not connected now), or a folder on the way it may not open, is "could not tell": no row, and
    /// no removal. Only a drive that is there with the folder not on it is gone, and the question names that folder.
    /// </summary>
    [Fact]
    public void AnEntryIsGoneOnlyWhenItsDriveIsThereAndTheFolderIsNot()
    {
        _pc.Links[CodexLink] = @"E:\Tools\codex\bin";
        _pc.AbsentDrives.Add("E:");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind is LinkKind.PathEntry or LinkKind.PathEntryGone);
        var gone = new LinkFinding { Kind = LinkKind.PathEntryGone, Scope = EnvScope.User, Index = 1, Entry = Codex, Link = Codex };
        Assert.Equal((false, AppDataLinkFixes.ChangedSince), Fixes().Remove(gone, "a test"));

        _pc.AbsentDrives.Clear();
        _pc.Real.Add(@"E:\Tools\readme.txt");
        _pc.Denied.Add(@"E:\Tools");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind is LinkKind.PathEntry or LinkKind.PathEntryGone);
        Assert.Equal((false, AppDataLinkFixes.ChangedSince), Fixes().Remove(gone, "a test"));
        Assert.Empty(_pc.Values.Writes);
        Assert.Empty(_f.Records.All());

        _pc.Denied.Clear();
        var row = Row(LinkKind.PathEntryGone);
        Assert.True(row.CanFix);
        Assert.Contains(@"E:\Tools\codex\bin", LinkWords.RemoveQuestion(row));
    }

    /// <summary>
    /// codex update puts its link back in front after each fix. Fixed again, the entry is removed rather than its folder
    /// written a second time, as that folder is on the PATH already; and the two changes put back in either order leave the
    /// entry on the PATH once and PoolWatch's folder not at all.
    /// </summary>
    [Fact]
    public void AFolderAlreadyOnThePathIsNeverWrittenTwiceAndTwoFixesPutBackInEitherOrder()
    {
        const string fixedPath = @"%USERPROFILE%\.local\bin;" + CodexTarget + @";C:\Tools";
        for (int order = 0; order < 2; order++)
        {
            _pc.Values.Set(EnvScope.User, "Path", UserPath, expand: true);
            Assert.True(Fixes().Replace(Row(LinkKind.PathEntry), "a test").Ok);
            _pc.Values.Set(EnvScope.User, "Path", Codex + ";" + fixedPath, expand: true);
            var again = Row(LinkKind.PathEntry);
            Assert.Equal(0, again.Index);
            Assert.Contains("on your PATH already", LinkWords.ReplaceQuestion(again));
            var (ok, message) = Fixes().Replace(again, "a test");
            Assert.True(ok, message);
            Assert.Equal(fixedPath, _pc.Values.Text(EnvScope.User, "Path")); // the folder once, never twice
            Assert.Equal(2, _f.Records.All().Count);

            var source = Source();
            var rows = source.List().Where(c => c.Kind == ChangeKind.PathEntry).ToList();
            var replaced = rows.Single(c => c.Id.StartsWith("path:", StringComparison.Ordinal));
            var removed = rows.Single(c => c.Id.StartsWith("path-removed:", StringComparison.Ordinal));
            foreach (var c in order == 0 ? new[] { replaced, removed } : new[] { removed, replaced })
            {
                var result = source.Undo(c, confirmed: false);
                Assert.True(result.Done, result.Message);
            }
            var entries = PathList.Split(_pc.Values.Text(EnvScope.User, "Path")!);
            Assert.Equal(1, entries.Count(e => e == Codex));
            Assert.DoesNotContain(CodexTarget, entries);
            if (order == 0) Assert.Equal(UserPath, _pc.Values.Text(EnvScope.User, "Path")); // the older first: exactly as it was
            Assert.Empty(_f.Records.All());
        }
    }

    /// <summary>
    /// A variable set to something that is not a plain local folder (a share, or a %NAME% not filled in) already points
    /// away from AppData as far as PoolWatch can tell: it is left alone, offered no fix, and never told about as come back.
    /// </summary>
    [Fact]
    public void AVariableSetToAShareOrAnUnfilledNameIsLeftAlone()
    {
        string python = _pc.AppData + @"\uv\python";
        _pc.Link(python + @"\cpython-3.12-windows-x86_64-none", "cpython-3.12.13-windows-x86_64-none", targetThere: false);
        _pc.Real.Add(python + @"\cpython-3.12.13-windows-x86_64-none\python.exe");
        _pc.Env[LinkFinder.UvPythons] = @"\\nas\uv\python";
        var uv = Row(LinkKind.Uv);
        Assert.True(uv.Moved);
        Assert.Equal([LinkFinder.UvCache], uv.EnvSettings.Select(s => s.Name));

        _pc.Real.Add(_pc.LocalFolder + @"\pnpm\global\5\node_modules\x");
        _pc.Env[LinkFinder.PnpmHome] = @"%PNPM_ROOT%\pnpm";
        _pc.Env[LinkFinder.CodexInstall] = @"\\nas\codex\bin";
        Assert.DoesNotContain(Scan().Findings, f => f.Kind is LinkKind.Pnpm or LinkKind.Codex);

        _pc.Env.Remove(LinkFinder.PnpmHome);
        Assert.True(Fixes().SetVariables(Row(LinkKind.Pnpm), "a test").Ok);
        _pc.Env[LinkFinder.PnpmHome] = @"\\nas\pnpm";
        Assert.Empty(AppDataLinkFinder.Back(Scan(), _f.Records.All()));
    }

    [Fact]
    public void ACodexBinFolderThatIsNotALinkIsNotOffered()
    {
        _pc.Links.Remove(CodexLink);
        _pc.Real.Add(CodexLink + @"\codex.exe");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.Codex);
    }

    /// <summary>Claude Code's temp folder under a TEMP moved off AppData: nobody measured that case, so it is not offered.</summary>
    [Fact]
    public void ClaudeCodesTempFolderOutsideAppDataIsNotOffered()
    {
        _pc.Env["TEMP"] = @"D:\Temp";
        _pc.Link(@"D:\Temp\claude", @"D:\Scratch");
        Assert.DoesNotContain(Scan().Findings, f => f.Kind == LinkKind.ClaudeTemp);
    }

    /// <summary>The prompt names no folder of the PC outside your profile and AppData: a venv elsewhere is not named in it.</summary>
    [Fact]
    public void AVenvOutsideYourProfileIsNotNamedInThePrompt()
    {
        string python = _pc.AppData + @"\uv\python";
        _pc.Link(python + @"\cpython-3.13-windows-x86_64-none", python + @"\cpython-3.13.5-windows-x86_64-none");
        const string venv = @"D:\clients\acme\.venv";
        _pc.AddFile(venv + @"\pyvenv.cfg", "home = " + python + "\\cpython-3.13-windows-x86_64-none\r\n");
        _pc.Values.Set(EnvScope.User, "Path", venv + @"\Scripts;" + UserPath, expand: true);
        string prompt = LinkWords.GuidePrompt(Row(LinkKind.Venv), _pc.Profile);
        Assert.DoesNotContain("acme", prompt);
        Assert.DoesNotContain(@"D:\", prompt);
        Assert.Contains(@"%APPDATA%\uv\python\cpython-3.13-windows-x86_64-none", prompt);
    }

    /// <summary>With CLAUDE_CONFIG_DIR set, the hooks are read from its settings.json: the row and the prompt name that file (outside the profile, the prompt does not).</summary>
    [Fact]
    public void TheHooksRowNamesTheSettingsFileItRead()
    {
        _pc.Link(_pc.AppData + @"\uv\python\cpython-3.12-windows-x86_64-none", "cpython-3.12.13-windows-x86_64-none", targetThere: false);
        _pc.Real.Add(_pc.AppData + @"\uv\python\cpython-3.12.13-windows-x86_64-none\python.exe");
        const string settings = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "python3 stop.py" } ] } ] } }""";
        string config = _pc.Profile + @"\cc-config";
        _pc.Env["CLAUDE_CONFIG_DIR"] = config;
        _pc.AddFile(config + @"\settings.json", settings);
        var hook = Row(LinkKind.Hook);
        Assert.Contains(@"%USERPROFILE%\cc-config\settings.json", LinkWords.Details(hook));
        Assert.Contains(@"%USERPROFILE%\cc-config\settings.json", LinkWords.GuidePrompt(hook, _pc.Profile));

        _pc.Env["CLAUDE_CONFIG_DIR"] = @"D:\clients\acme\cc";
        _pc.AddFile(@"D:\clients\acme\cc\settings.json", settings);
        Assert.DoesNotContain("acme", LinkWords.GuidePrompt(Row(LinkKind.Hook), _pc.Profile));
    }

    // ------------------------------------------------------------------ the real read of a small file, in the test's own TEMP folder

    private static string ReadRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A plain file is read, its byte-order mark left out and bytes that are not UTF-8 read as U+FFFD; a folder, or nothing there, is not read.</summary>
    [Fact]
    public void TheRealReadReadsAPlainFileWithoutItsByteOrderMark()
    {
        string root = ReadRoot();
        try
        {
            string file = Path.Combine(root, "pyvenv.cfg");
            byte[] bom = [0xEF, 0xBB, 0xBF];
            File.WriteAllBytes(file, bom.Concat(System.Text.Encoding.UTF8.GetBytes("home = C:\\Python\r\n")).ToArray());
            var pc = new WindowsLinkSystem();
            Assert.Equal("home = C:\\Python\r\n", pc.ReadText(file, 64));
            File.WriteAllBytes(file, new byte[] { (byte)'a', 0xFF, (byte)'b' });
            Assert.Equal("a\uFFFDb", pc.ReadText(file, 64));
            Assert.Null(pc.ReadText(Path.Combine(root, "none.cfg"), 64));
            Assert.Null(pc.ReadText(root, 64)); // a folder
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>A file is read whole, or, when it is longer than PoolWatch reads, not at all.</summary>
    [Fact]
    public void TheRealReadReadsAFileWholeOrNotAtAll()
    {
        string root = ReadRoot();
        try
        {
            string file = Path.Combine(root, "settings.json");
            var pc = new WindowsLinkSystem();
            File.WriteAllText(file, new string('a', 64));
            Assert.Equal(new string('a', 64), pc.ReadText(file, 64));
            File.WriteAllText(file, new string('a', 65));
            Assert.Null(pc.ReadText(file, 64));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A link at the path itself is never read, whatever it holds: a file marked as a reparse point (with data of its own),
    /// and a symbolic link to a plain file where Windows lets the test make one (it needs a privilege or Developer Mode).
    /// </summary>
    [Fact]
    public void TheRealReadNeverReadsThroughALinkAtThePath()
    {
        string root = ReadRoot();
        try
        {
            string real = Path.Combine(root, "real.cfg"), marked = Path.Combine(root, "marked.cfg"), link = Path.Combine(root, "link.cfg");
            File.WriteAllText(real, "home = C:\\Python\n");
            File.WriteAllText(marked, "home = C:\\Elsewhere\n");
            Links.MarkAsReparsePoint(marked);
            var pc = new WindowsLinkSystem();
            Assert.Equal("home = C:\\Python\n", pc.ReadText(real, 64));
            Assert.Null(pc.ReadText(marked, 64));
            bool made;
            try
            {
                File.CreateSymbolicLink(link, real);
                made = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { made = false; }
            if (made) Assert.Null(pc.ReadText(link, 64));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
