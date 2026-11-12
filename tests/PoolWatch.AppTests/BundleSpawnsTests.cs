using PoolWatch.Analysis;

namespace PoolWatch.AppTests;

/// <summary>
/// The vendor bundle's spawns.csv is the most recently ACTIVE storm folder's, not the newest by name: one folder takes every
/// storm of its program that comes within an hour of the one before, so a folder made in the morning can be the one still
/// in use in the evening (BundleBuilder.LatestStormSpawns); and it holds that file's whole rows only, of a storm folder
/// and a file that were not links when they were looked at, and never past its bound (BundleBuilder.StormSpawns).
/// </summary>
public sealed class BundleSpawnsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-bundle-spawns-" + Guid.NewGuid().ToString("N"))).FullName;
    /// <summary>A folder of the test's own outside the incidents folder, where a planted link leads.</summary>
    private readonly string _outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-bundle-outside-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (string link in _links) Links.DeleteLinkOnly(link); // the link itself, never what it leads to
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private string Storm(string name, DateTime written)
    {
        string dir = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        string sp = Path.Combine(dir, "spawns.csv");
        File.WriteAllText(sp, "child_pid,image\r\n1,git.exe\r\n");
        File.SetLastWriteTimeUtc(sp, written);
        return dir;
    }

    [Fact]
    public void TheMostRecentlyActiveStormFoldersSpawnsAreTakenNotTheNewestNamed()
    {
        var t = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        string morning = Storm("20261109-081500-SpawnStorm-bash", t);                     // a Claude Code session's bash, storming all day
        string noon = Storm("20261109-120000-SpawnStorm-node", t.AddHours(-8));           // newer by name, idle since noon
        // Held in memory: the morning folder was active last.
        Assert.Equal(Path.Combine(morning, "spawns.csv"), BundleBuilder.LatestStormSpawns([(noon, 1_000), (morning, 9_000), (null, 99_000)], _dir));
        // None held (PoolWatch started again): the one whose spawns.csv was written last.
        Assert.Equal(Path.Combine(morning, "spawns.csv"), BundleBuilder.LatestStormSpawns([], _dir));
        File.SetLastWriteTimeUtc(Path.Combine(noon, "spawns.csv"), t.AddMinutes(5));
        Assert.Equal(Path.Combine(noon, "spawns.csv"), BundleBuilder.LatestStormSpawns([], _dir));
        Assert.Null(BundleBuilder.LatestStormSpawns([], Path.Combine(_dir, "none")));
    }

    [Fact]
    public void TheBundleReadsTheStormsSpawnsUpToItsLastWholeRow()
    {
        string storm = Storm("20261109-081500-SpawnStorm-bash", new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(storm, "spawns.csv"), "child_pid,image\r\n1,git.exe\r\n2,ba"); // read in the moment of an append
        Assert.Equal("child_pid,image\r\n1,git.exe\r\n", BundleBuilder.StormSpawns([], _dir));
        string bundle = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Analysis", "BundleBuilder.cs"));
        Assert.Contains("if (StormSpawns(held, engine.Paths.IncidentsDir) is { } rows) files[\"spawns.csv\"] = rows;", bundle);
        Assert.Contains("LatestStormSpawns(held, incidentsDir) is { } sp ? Core.Util.Csv.ReadCompleteRows(sp, MaxSpawnsBytes) : null;", bundle);
        Assert.DoesNotContain("new FileInfo(sp).Length", bundle); // its size is never taken from a look at the file before the read
        Assert.DoesNotContain(".Order(StringComparer.Ordinal).LastOrDefault()", bundle);
    }

    /// <summary>
    /// The bundle takes a spawns.csv of at most 5 MB and leaves a larger one out, as before; the size is what the read
    /// itself finds, through the handle it opened (it reads the bound and one byte more at most), not a length asked of the
    /// file beforehand. A file of exactly the bound goes in, its whole rows; one byte more and it is left out.
    /// </summary>
    [Fact]
    public void TheBundleLeavesOutASpawnsFileOverItsBound()
    {
        Assert.Equal(5 << 20, BundleBuilder.MaxSpawnsBytes);
        string storm = Storm("20261109-081500-SpawnStorm-bash", new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc));
        string sp = Path.Combine(storm, "spawns.csv");
        const string Header = "child_pid,image\r\n", Row = "1,git.exe\r\n";
        string whole = Header + string.Concat(Enumerable.Repeat(Row, (BundleBuilder.MaxSpawnsBytes - Header.Length - 5) / Row.Length));
        string atBound = whole + new string('x', BundleBuilder.MaxSpawnsBytes - whole.Length); // its last row cut short
        File.WriteAllText(sp, atBound);
        Assert.Equal(BundleBuilder.MaxSpawnsBytes, new FileInfo(sp).Length);
        Assert.Equal(whole, BundleBuilder.StormSpawns([], _dir));
        File.AppendAllText(sp, "x");
        Assert.Null(BundleBuilder.StormSpawns([], _dir));
        Assert.Null(BundleBuilder.StormSpawns([(storm, 1_000)], _dir)); // held in memory or found on disk
    }

    /// <summary>
    /// The bundle is made as administrator and kept where the signed-in account can read it, and the incidents folder can
    /// be one that someone who is not an administrator may write (a run given --out). A storm folder there that is a link
    /// (here a junction to another folder), or whose spawns.csv is one, is passed over when it is looked at, however
    /// lately its file was written and whether PoolWatch holds it in memory or finds it on disk, so what a link that is
    /// there at that look leads to is not read into the bundle (a link put in place after the look is not found:
    /// SECURITY.md, Known limitations). Everything here is in the test's own TEMP folders.
    /// </summary>
    [Fact]
    public void AStormFolderOrASpawnsFileThatIsALinkIsNeverReadIntoTheBundle()
    {
        var t = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        string real = Storm("20261109-081500-SpawnStorm-bash", t);
        // Another folder's spawns.csv, written last, behind a junction named like a storm folder.
        string elsewhere = Path.Combine(_outside, "spawns.csv");
        File.WriteAllText(elsewhere, "child_pid,image\r\n7,from-another-folder.exe\r\n");
        File.SetLastWriteTimeUtc(elsewhere, t.AddHours(1));
        string planted = Path.Combine(_dir, "20261109-235900-SpawnStorm-zz");
        Links.Junction(planted, _outside);
        _links.Add(planted);
        Assert.True(File.Exists(Path.Combine(planted, "spawns.csv"))); // the link leads to it
        Assert.Equal(Path.Combine(real, "spawns.csv"), BundleBuilder.LatestStormSpawns([], _dir));
        Assert.Equal(Path.Combine(real, "spawns.csv"), BundleBuilder.LatestStormSpawns([(planted, 9_000), (real, 1_000)], _dir));
        Assert.Equal("child_pid,image\r\n1,git.exe\r\n", BundleBuilder.StormSpawns([], _dir));
        Assert.Equal("child_pid,image\r\n1,git.exe\r\n", BundleBuilder.StormSpawns([(planted, 9_000)], _dir));
        // A real storm folder whose spawns.csv is itself a link (marked as one: a symbolic link needs a privilege the test may not hold).
        string linked = Storm("20261109-235959-SpawnStorm-zzz", t.AddHours(2));
        Links.MarkAsReparsePoint(Path.Combine(linked, "spawns.csv"));
        Assert.Equal(Path.Combine(real, "spawns.csv"), BundleBuilder.LatestStormSpawns([], _dir));
        Assert.Equal(Path.Combine(real, "spawns.csv"), BundleBuilder.LatestStormSpawns([(linked, 9_000)], _dir));
        // A folder PoolWatch holds that is not below the incidents folder is passed over as well.
        Assert.Equal(Path.Combine(real, "spawns.csv"), BundleBuilder.LatestStormSpawns([(_outside, 9_000)], _dir));
        // Only links left: nothing for the bundle.
        Directory.Delete(real, recursive: true);
        Assert.Null(BundleBuilder.LatestStormSpawns([(planted, 9_000), (linked, 8_000), (_outside, 7_000)], _dir));
        Assert.Null(BundleBuilder.StormSpawns([], _dir));
    }
}
