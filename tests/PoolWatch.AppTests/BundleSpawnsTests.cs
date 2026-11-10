using PoolWatch.Analysis;

namespace PoolWatch.AppTests;

/// <summary>
/// The vendor bundle's spawns.csv is the most recently ACTIVE storm folder's, not the newest by name: one folder takes every
/// storm of its program that comes within an hour of the one before, so a folder made in the morning can be the one still
/// in use in the evening (BundleBuilder.LatestStormSpawns); and it holds that file's whole rows only.
/// </summary>
public sealed class BundleSpawnsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-bundle-spawns-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

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
        string bundle = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Analysis", "BundleBuilder.cs"));
        Assert.Contains("if (LatestStormSpawns(held, engine.Paths.IncidentsDir) is { } sp && new FileInfo(sp).Length <= 5 << 20)", bundle);
        Assert.Contains("files[\"spawns.csv\"] = Core.Util.Csv.ReadCompleteRows(sp);", bundle);
        Assert.DoesNotContain(".Order(StringComparer.Ordinal).LastOrDefault()", bundle);
    }
}
