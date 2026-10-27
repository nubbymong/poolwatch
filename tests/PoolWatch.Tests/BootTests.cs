using System.IO.Compression;
using PoolWatch.Core;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class BootTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 26, 6, 30, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-boots-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Sample S(DateTime t, long npMb, long fileMb) =>
        new(t, new PoolTotals(500UL << 20, (ulong)npMb << 20, 0, 0),
            [new PoolTagEntry(PoolTag.Parse("File"), 0, 0, 0, 10, 1, (ulong)fileMb << 20), new PoolTagEntry(PoolTag.Parse("MmSt"), 1, 0, 300UL << 20, 0, 0, 0)], [])
        { System = new SystemCounters(8UL << 30, 32UL << 30, 0, 0, 90_000, 250) };

    [Fact]
    public void BaselineOncePerBootSummaryAcrossRestartsAndBoots()
    {
        var boot1 = T0;
        var tr = new BootTracker(_dir, p => p);
        tr.Observe(S(boot1.AddMinutes(3), 400, 80), TimeSpan.FromMinutes(3), 0);
        Assert.NotNull(tr.Baseline);
        Assert.True(tr.Baseline!.NearBoot);
        Assert.Equal(400L << 20, tr.Baseline.NonPaged);
        Assert.Equal("File", tr.Baseline.Tags[1].Tag);
        tr.Observe(S(boot1.AddHours(5), 2400, 1900), TimeSpan.FromHours(5).Add(TimeSpan.FromSeconds(20)), 2); // uptime jitter: same boot
        tr.Save(boot1.AddHours(5), ended: true);
        Assert.Single(Directory.GetDirectories(_dir));

        // PoolWatch restarts in the same boot: the baseline is not retaken, the summary continues.
        var tr2 = new BootTracker(_dir, p => p);
        tr2.Observe(S(boot1.AddHours(6), 2000, 1500), TimeSpan.FromHours(6), 0);
        Assert.Equal(boot1.AddMinutes(3), tr2.Baseline!.TimeUtc);
        Assert.Equal(2400L << 20, tr2.Current!.PeakNonPaged);
        Assert.Equal(2, tr2.Current.Incidents);
        tr2.Save(boot1.AddHours(6), ended: true);

        // Another restart whose boot time computes one minute later (minute boundary, clock correction): still the same boot.
        var tr3 = new BootTracker(_dir, p => p);
        tr3.Observe(S(boot1.AddHours(7), 2000, 1500), TimeSpan.FromHours(7) - TimeSpan.FromSeconds(70), 3);
        Assert.Single(Directory.GetDirectories(_dir));
        Assert.Equal(5, tr3.Current!.Incidents); // 2 from the first run + 3 from this one
        tr3.Save(boot1.AddHours(7), ended: true);

        // Next boot.
        var boot2 = boot1.AddDays(1);
        tr2.Observe(S(boot2.AddMinutes(50), 420, 85), TimeSpan.FromMinutes(50), 0);
        Assert.False(tr2.Baseline!.NearBoot);
        tr2.Save(boot2.AddMinutes(50));
        var all = tr2.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(6.98, all[0].UptimeHoursAtLastSeen);
        Assert.Equal("File", all[0].TopTagsAtPeak[0].Tag);
        string table = BootTracker.Table(all);
        Assert.Contains("| 2.34 GB | 500 MB |", table);
        Assert.Contains("(running or not closed cleanly)", table);
        Assert.Contains("Previous boots: peak non-paged 2.34 GB after 7 h.", tr2.PreviousLine());
        Assert.Equal(2, tr2.Baselines().Count);
    }

    [Fact]
    public void BundleIsRedactedByDefaultAndListsItsParts()
    {
        var files = new Dictionary<string, string>
        {
            ["README.md"] = VendorBundle.Readme(true, T0, "1.1.0"),
            ["system.json"] = "{\"machine\": \"ALICE-PC\", \"home\": \"C:\\\\Users\\\\alice\\\\AppData\"}",
            ["tags/now.csv"] = VendorBundle.TagCsv([new TagSize("File", 0, 5 << 20), new TagSize("MmSt", 300 << 20, 0)]),
        };
        var zip = VendorBundle.Write(_dir, T0, files, new RedactionInfo("alice", "ALICE-PC", [@"C:\Users\alice"]), p => p);
        Assert.EndsWith("-redacted.zip", zip);
        using var z = ZipFile.OpenRead(zip);
        Assert.Equal(["README.md", "system.json", "tags/now.csv"], z.Entries.Select(e => e.FullName).Order());
        string sys = new StreamReader(z.GetEntry("system.json")!.Open()).ReadToEnd();
        Assert.DoesNotContain("alice", sys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<MACHINE>", sys);
        Assert.Contains("<HOME>", sys);
        string readme = new StreamReader(z.GetEntry("README.md")!.Open()).ReadToEnd();
        Assert.Contains("| `samples.csv` |", readme);
        Assert.Contains("Redacted:", readme);
        string tags = new StreamReader(z.GetEntry("tags/now.csv")!.Open()).ReadToEnd();
        Assert.StartsWith("tag,paged_bytes,nonpaged_bytes,total_bytes\nMmSt,314572800,0,314572800\n", tags);
    }
}
