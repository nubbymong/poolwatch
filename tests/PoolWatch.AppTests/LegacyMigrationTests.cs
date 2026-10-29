using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// S3 (review round 2): the one-time move of the old per-user data folder into the admin-only root. Everything runs in
/// the test's own TEMP folder; the links are planted there with <c>cmd /c mklink</c> (arguments as a list) and removed
/// (the link entry first, never through it) before anything else is deleted.
/// </summary>
public sealed class LegacyMigrationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pw-mig-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();
    private string Legacy => Path.Combine(_base, "old");
    private string Data => Path.Combine(_base, "new");
    private string Outside => Path.Combine(_base, "outside");

    public LegacyMigrationTests()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "keep.txt"), "must survive");
    }

    public void Dispose()
    {
        bool linksGone = true;
        foreach (var l in _links) linksGone &= Links.DeleteLinkOnly(l);
        if (linksGone) try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private LegacyMigration.Result Run(List<string>? log = null) =>
        new LegacyMigration(Legacy, Data, new PathGuard(Data, null).CheckReal, s => log?.Add(s)).Run();

    private void Write(string rel, string text)
    {
        string p = Path.Combine(Legacy, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private void AssertNothingMoved()
    {
        // Nothing copied and no marker: only the attempt counter (review round 3: a refused run is retried, at most 5 times).
        Assert.Equal([LegacyMigration.AttemptsName], Directory.EnumerateFileSystemEntries(Data).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(Legacy, LegacyMigration.ReadmeName)));  // no README
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(Outside, "keep.txt")));
    }

    // ------------------------------------------------------------------ review round 3

    [Fact]
    public void AFileThatCouldNotBeCopiedLeavesNoMarkerAndIsRetriedNextTime()
    {
        Write("settings.json", "{}");
        Write(@"data\20261020\tags.csv", "time,tag");
        string busy = Path.Combine(Legacy, @"data\20261020\tags.csv");
        LegacyMigration.Result first;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None)) first = Run(); // in use: cannot be read now
        Assert.False(first.Complete);
        Assert.Contains(first.Failed, f => f.StartsWith(@"data\20261020\tags.csv", StringComparison.Ordinal));
        Assert.Equal(1, first.Attempt);
        Assert.False(File.Exists(Path.Combine(Data, LegacyMigration.MarkerName)));    // not done: no marker
        Assert.False(File.Exists(Path.Combine(Legacy, LegacyMigration.ReadmeName)));
        Assert.Contains("attempt 1 of 5", first.Summary());

        var second = Run();                                                             // free again: the rest moves now
        Assert.True(second.Complete);
        Assert.Equal(2, second.Attempt);
        Assert.Equal("time,tag", File.ReadAllText(Path.Combine(Data, @"data\20261020\tags.csv")));
        Assert.True(File.Exists(Path.Combine(Data, LegacyMigration.MarkerName)));
        Assert.True(File.Exists(Path.Combine(Legacy, LegacyMigration.ReadmeName)));
        Assert.False(File.Exists(Path.Combine(Data, LegacyMigration.AttemptsName)));
    }

    [Fact]
    public void RetriesAreBounded()
    {
        Write(@"data\20261020\tags.csv", "time,tag");
        string busy = Path.Combine(Legacy, @"data\20261020\tags.csv");
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (int i = 1; i < LegacyMigration.MaxAttempts; i++) Assert.False(Run().GaveUp);
            var last = Run();
            Assert.True(last.GaveUp);
            Assert.Contains($"gave up after {LegacyMigration.MaxAttempts} attempts", last.Summary());
        }
        Assert.True(File.Exists(Path.Combine(Data, LegacyMigration.MarkerName)));     // no more runs
        Assert.True(File.Exists(busy));                                                // the rest stays where it was
        Assert.True(Run().NothingToDo);
    }

    [Fact]
    public void TemporaryCopiesLeftByACutShortRunAreRemoved()
    {
        Write("settings.json", "{}");
        Directory.CreateDirectory(Path.Combine(Data, "data", "20261020"));
        File.WriteAllText(Path.Combine(Data, "data", "20261020", "tags.csv.migrating"), "half");
        File.WriteAllText(Path.Combine(Data, "poolwatch.log.migrating"), "half");
        Assert.True(Run().Complete);
        Assert.Empty(Directory.EnumerateFiles(Data, "*.migrating", SearchOption.AllDirectories));
    }

    private string? MigrateSettings() =>
        new LegacyMigration(Legacy, Data, new PathGuard(Data, null).CheckReal, _ => { }).MigrateSettings(json => PoolWatch.Core.Settings.Parse(json).ToJson());

    [Fact]
    public void SettingsAreMovedFirstCheckedAndClampedNeverCarriedOverAsTheyAre()
    {
        Write("settings.json", "{ \"sampleIntervalSeconds\": 0.001, \"captureSeconds\": 99999, \"retentionDays\": 5000, " +
                               "\"cpuTraceEnabled\": true, \"evilUnknownField\": \"x\" }");
        Assert.Contains("migrated", MigrateSettings());
        string moved = File.ReadAllText(Path.Combine(Data, "settings.json"));
        var s = PoolWatch.Core.Settings.Load(Path.Combine(Data, "settings.json"));
        Assert.Equal(1, s.SampleIntervalSeconds);      // clamped like any load
        Assert.Equal(300, s.CaptureSeconds);
        Assert.Equal(90, s.RetentionDays);
        Assert.False(s.CpuTraceEnabled);                // an old file's default, migrated like any default settings.json
        Assert.DoesNotContain("evilUnknownField", moved);
        Assert.False(File.Exists(Path.Combine(Legacy, "settings.json"))); // removed from the old folder
        Assert.Null(MigrateSettings());                 // once (the new root has its own now)
    }

    [Fact]
    public void SettingsThatAreALinkOrNotValidAreNotMoved()
    {
        string target = Path.Combine(Outside, "secret.json");
        File.WriteAllText(target, "{}");
        Links.HardLink(Path.Combine(Legacy, "settings.json"), target);
        Assert.Contains("more than one name", MigrateSettings());
        Assert.False(File.Exists(Path.Combine(Data, "settings.json")));
        File.Delete(Path.Combine(Legacy, "settings.json"));

        Write("settings.json", "{ not json");
        Assert.Contains("not valid", MigrateSettings());
        Assert.False(File.Exists(Path.Combine(Data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json"))); // left where it was
    }

    [Fact]
    public void RefusesAJunctionAnywhereUnderTheOldFolder()
    {
        Write("settings.json", "{}");
        Write(@"incidents\20261020-111111-Reff\report.md", "# report");
        string link = Path.Combine(Legacy, "incidents", "evil");
        Links.Junction(link, Outside);
        _links.Add(link);

        var r = Run();

        Assert.NotNull(r.Refused);
        Assert.Contains("evil", r.Refused);
        Assert.Contains("link", r.Refused);
        AssertNothingMoved();
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));              // nothing deleted either
        Assert.True(File.Exists(Path.Combine(Legacy, @"incidents\20261020-111111-Reff\report.md")));
    }

    [Fact]
    public void RefusesWhenTheOldFolderItselfIsAJunction()
    {
        Directory.Delete(Legacy);
        string real = Path.Combine(_base, "real-old");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "settings.json"), "{}");
        Links.Junction(Legacy, real);
        _links.Add(Legacy);

        var r = Run();

        Assert.NotNull(r.Refused);
        Assert.Contains("link", r.Refused);
        Assert.Equal([LegacyMigration.AttemptsName], Directory.EnumerateFileSystemEntries(Data).Select(Path.GetFileName)); // nothing copied, no marker
        Assert.True(File.Exists(Path.Combine(real, "settings.json")));
    }

    [Fact]
    public void RefusesAHardLinkedFile()
    {
        Write("settings.json", "{}");
        Directory.CreateDirectory(Path.Combine(Legacy, "data", "20261020"));
        string target = Path.Combine(Outside, "secret.csv");
        File.WriteAllText(target, "not PoolWatch's");
        Links.HardLink(Path.Combine(Legacy, "data", "20261020", "tags.csv"), target);

        var r = Run();

        Assert.NotNull(r.Refused);
        Assert.Contains("hard link", r.Refused);
        AssertNothingMoved();
        Assert.Equal("not PoolWatch's", File.ReadAllText(target));
    }

    [Fact]
    public void MovesOnlyPoolWatchsOwnDataAndLeavesAReadme()
    {
        Write("settings.json", "{ \"sampleIntervalSeconds\": 10 }");
        Write("poolwatch.log", "log line");
        Write(@"data\20261020\tags.csv", "time,tag");
        Write(@"incidents\20261020-111111-Reff\report.md", "# report");
        Write("service-changes.json", "[]");                  // never from a user-writable folder
        Write(@"cache\tagscan.json", "{}");                   // rebuilt
        Write(@"symbols\ntkrnlmp.pdb", "pdb");                // never from a user-writable folder
        Write(@"requests\mark.txt", "note");
        Write("tool.exe", "MZ");                             // not PoolWatch's
        string big = Path.Combine(Legacy, @"incidents\20261020-111111-Reff\capture.etl");
        using (var f = new FileStream(big, FileMode.Create)) f.SetLength(LegacyMigration.MaxFileBytes + 1);
        var log = new List<string>();

        var r = Run(log);

        Assert.Null(r.Refused);
        Assert.Empty(r.Failed);
        Assert.Equal(4, r.Copied);
        Assert.Equal(4, r.Deleted);
        foreach (var (rel, text) in new[] { ("settings.json", "{ \"sampleIntervalSeconds\": 10 }"), ("poolwatch.log", "log line"),
                     (@"data\20261020\tags.csv", "time,tag"), (@"incidents\20261020-111111-Reff\report.md", "# report") })
        {
            Assert.Equal(text, File.ReadAllText(Path.Combine(Data, rel)));
            Assert.False(File.Exists(Path.Combine(Legacy, rel)), rel + " is still in the old folder");
        }
        foreach (var rel in new[] { "service-changes.json", @"cache\tagscan.json", @"symbols\ntkrnlmp.pdb", @"requests\mark.txt", "tool.exe" })
        {
            Assert.True(File.Exists(Path.Combine(Legacy, rel)), rel + " should stay");
            Assert.False(File.Exists(Path.Combine(Data, rel)), rel + " should not be copied");
        }
        Assert.True(File.Exists(big));
        Assert.Contains(r.Left, l => l.StartsWith(@"incidents\20261020-111111-Reff\capture.etl (over 50 MB", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Legacy, "data")));                // emptied folders go
        Assert.True(Directory.Exists(Path.Combine(Legacy, "incidents")));            // the trace keeps its folder
        Assert.Empty(Directory.EnumerateFiles(Data, "*.migrating", SearchOption.AllDirectories));
        var readme = File.ReadAllLines(Path.Combine(Legacy, LegacyMigration.ReadmeName));
        Assert.Single(readme);
        Assert.Contains(Data, readme[0]);
        Assert.True(File.Exists(Path.Combine(Data, LegacyMigration.MarkerName)));
        Assert.True(Run().NothingToDo);                                              // once only
    }

    [Fact]
    public void AnExistingFileInTheNewFolderIsNeverOverwritten()
    {
        Write("settings.json", "old");
        File.WriteAllText(Path.Combine(Data, "settings.json"), "new");

        var r = Run();

        Assert.Null(r.Refused);
        Assert.Equal(0, r.Copied);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Data, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "settings.json"))); // not deleted: it was not copied
        Assert.Contains(r.Left, l => l.Contains("different file"));
    }

    [Theory]
    [InlineData("settings.json", 10, null)]
    [InlineData(@"data\20261020\processes.csv", 10, null)]
    [InlineData(@"data\20261020\findings.jsonl", 10, null)]
    [InlineData(@"incidents\x\timeline.png", 10, null)]
    [InlineData(@"exports\x.zip", 10, null)]
    [InlineData(@"boots\202610200800\baseline.json", 10, null)]
    [InlineData(@"incidents\x\capture.etl", LegacyMigration.MaxFileBytes + 1, "over 50 MB")]
    [InlineData(@"incidents\x\poolwatch.wprp", 10, "not PoolWatch's data")]
    [InlineData(@"incidents\x\wpr-temp\a.etl", 10, "not PoolWatch's data")]
    [InlineData(@"data\x.dll", 10, "not PoolWatch's data")]
    [InlineData(@"cache\clears.json", 10, "a cache (rebuilt)")]
    [InlineData("logon-task.xml", 10, "not PoolWatch's data")]
    // Undo puts back what these hold as an administrator: never taken from a folder an unelevated program could write.
    [InlineData("applied-changes.json", 10, "not PoolWatch's data")]
    [InlineData(@"removed-tasks\Clear standby.xml", 10, "not PoolWatch's data")]
    [InlineData("service-changes.json", 10, "service records are never taken")]
    public void OnlyKnownFilesAreMoved(string rel, long length, string? why)
    {
        var got = LegacyMigration.WhyNotMoved(rel, length);
        if (why is null) Assert.Null(got);
        else Assert.StartsWith(why, got);
    }
}

/// <summary>S3 (review round 2): the data root's ACL as built in memory (never written by a test).</summary>
public class DataRootAclTests
{
    private static readonly SecurityIdentifier Owner = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private static List<FileSystemAccessRule> Rules(DirectorySecurity sec) =>
        sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();

    [Fact]
    public void AdministratorsAndSystemOnlyPlusTheOwnersRead()
    {
        var sec = Interop.DataRootSecurity(Owner);
        var rules = Rules(sec);
        Assert.True(sec.AreAccessRulesProtected);                                      // nothing inherited from %ProgramData%
        Assert.Equal("S-1-5-32-544", sec.GetOwner(typeof(SecurityIdentifier))!.Value); // owned by Administrators
        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-32-544" && r.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-18" && r.FileSystemRights == FileSystemRights.FullControl);
        var read = Assert.Single(rules, r => r.IdentityReference.Value == Owner.Value);
        Assert.Equal(AccessControlType.Allow, read.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute, read.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-5-32-545");  // no longer all Users
    }

    [Theory]
    [InlineData("S-1-5-32-545")]                                   // Users
    [InlineData("S-1-5-11")]                                       // Authenticated Users
    [InlineData("S-1-1-0")]                                        // Everyone
    [InlineData("S-1-5-18")]                                       // SYSTEM
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-513")]  // Domain Users / None
    public void AGroupIsNeverTheReader(string sid)
    {
        var rules = Rules(Interop.DataRootSecurity(new SecurityIdentifier(sid)));
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { "S-1-5-32-544", "S-1-5-18" }));
    }

    [Fact]
    public void AnEntraIdAccountIsOneAccount() =>
        Assert.True(Interop.IsOneAccount(new SecurityIdentifier("S-1-12-1-1111111111-2222222222-3333333333-4444444444")));

    [Fact]
    public void NoReaderMeansAdministratorsAndSystemOnly() => Assert.Equal(2, Rules(Interop.DataRootSecurity(null)).Count);
}

/// <summary>Plants junctions and hard links in the test's TEMP folder with <c>cmd /c mklink</c>; removes a link entry only.</summary>
internal static class Links
{
    public static void Junction(string link, string target) => Mklink("/J", link, target);
    public static void HardLink(string link, string target) => Mklink("/H", link, target);

    private static void Mklink(string kind, string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", kind, link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } throw new TimeoutException("mklink did not finish"); }
        if (p.ExitCode != 0 || !(Directory.Exists(link) || File.Exists(link)))
            throw new InvalidOperationException($"mklink {kind} failed (exit {p.ExitCode}): {output} {err.Result}");
    }

    /// <summary>Removes the link entry itself (non-recursive), never what it points at. True when nothing is left there.</summary>
    public static bool DeleteLinkOnly(string link)
    {
        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) return false;
                Directory.Delete(link, recursive: false);
            }
        }
        catch { }
        return !Directory.Exists(link) && !File.Exists(link);
    }
}
