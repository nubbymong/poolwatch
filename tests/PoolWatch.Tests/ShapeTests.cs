using System.Text.Json;
using PoolWatch.Core.Util;
using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

public class ShapeTests
{
    [Theory]
    [InlineData(5000, 3, AllocShape.LeakShape)]
    [InlineData(5000, 4900, AllocShape.Churn)]
    [InlineData(5000, 2500, AllocShape.GrowingWithChurn)]
    [InlineData(100, 400, AllocShape.Shrinking)]
    [InlineData(2, 1, AllocShape.Idle)]
    public void Classifies(double allocs, double frees, AllocShape expected) => Assert.Equal(expected, LeakShapes.Classify(allocs, frees));

    [Fact]
    public void RatesFromHistoryAcrossCounterWrap()
    {
        var h = new History();
        var t0 = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc);
        uint allocs = uint.MaxValue - 1000, frees = 500;
        for (int i = 0; i <= 10; i++)
        {
            var tag = new PoolTagEntry(PoolTag.Parse("WCsc"), 0, 0, 0, allocs, frees, 50_000_000UL + (ulong)i * 1_000_000);
            h.Add(new Sample(t0.AddMinutes(i), default, [tag], []) { System = new SystemCounters(1, 2, 3, 4, 5, 6) });
            allocs = unchecked(allocs + 6000);
            frees += 2;
        }
        var r = h.TagRatesBetween(PoolTag.Parse("WCsc"), 0, 10);
        Assert.Equal(6000, r.AllocsPerMin, 1);
        Assert.Equal(2, r.FreesPerMin, 1);
        Assert.Equal(AllocShape.LeakShape, r.Shape);
        Assert.Equal("allocations rising, frees flat (leak shape)", r.Text);
        Assert.Equal(5u, h.System[^1].Handles);
    }

    [Fact]
    public void RatesOfAFreshLeakStartWhereTheTagWasFirstStored()
    {
        // Starts at 4 KB (below the 16 KB storage threshold for raw rows) and grows 64 KB a minute.
        var h = new History();
        var t0 = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc);
        var tag = PoolTag.Parse("Abcd"); // an ordinary tag (WC* are always tracked)
        uint allocs = 10;
        for (int i = 0; i <= 10; i++)
        {
            ulong bytes = 4096 + (ulong)i * 65536;
            h.Add(new Sample(t0.AddMinutes(i), default, [new PoolTagEntry(tag, 0, 0, 0, allocs, 0, bytes), new PoolTagEntry(PoolTag.Parse("Big "), 0, 0, 0, 1, 0, 1 << 30)], []));
            allocs += 100;
        }
        Assert.Equal(1, h.FirstStoredIndex(tag, 0, 10));
        var r = h.TagRatesBetween(tag, 0, 10);
        Assert.Equal(100, r.AllocsPerMin, 1);
        Assert.Equal(AllocShape.LeakShape, r.Shape);
    }

    [Fact]
    public void CsvStartsANewFileWhenTheHeaderChanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "totals.csv");
            File.WriteAllText(path, "time,a\n1,2\n");
            using (var f = new CsvFile(path, "time,a,b")) f.Write(3, 4, 5);
            Assert.Equal("time,a\n1,2\n", File.ReadAllText(path));
            Assert.Equal(["time,a,b", "3,4,5"], File.ReadAllLines(Path.Combine(dir, "totals-2.csv")));
            using (var f = new CsvFile(path, "time,a,b")) f.Write(6, 7, 8); // same layout again: appended to -2
            Assert.Equal(3, File.ReadAllLines(Path.Combine(dir, "totals-2.csv")).Length);
            using (var f = new CsvFile(path, "time,a")) f.Write(9, 9);
            Assert.Equal(3, File.ReadAllLines(path).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DetectorCarriesRates()
    {
        var w = new World();
        long v = 10 * Settings.MB;
        for (int i = 0; i < 40; i++) { v += 400_000; w.Sample(("File", v)); }
        var d = new Detector(new Settings());
        d.Evaluate(w.History);
        Assert.Equal(AllocShape.Idle, d.Status[PoolTag.Parse("File")].Rates.Shape); // the synthetic world has flat counters
    }

    [Fact]
    public void BackgroundChurnDoesNotHideALeak()
    {
        // The CI runner: 59k leaked File handles a minute on top of ~19k/min background opens and closes.
        Assert.Equal(AllocShape.GrowingWithChurn, LeakShapes.Classify(78_456, 19_305));
        var before = new TagRates(19_500, 19_200, AllocShape.Churn);
        Assert.Equal(AllocShape.LeakShape, LeakShapes.Classify(78_456, 19_305, before));
        // Real extra frees (a cache that grows and is trimmed): still growing with churn.
        Assert.Equal(AllocShape.GrowingWithChurn, LeakShapes.Classify(78_456, 45_000, before));
        // An idle baseline changes nothing.
        Assert.Equal(AllocShape.GrowingWithChurn, LeakShapes.Classify(78_456, 19_305, new TagRates(0, 0, AllocShape.Idle)));
    }

    /// <summary>
    /// report.json's allocation shape and report.md of a File leak of 1,000 handles a sample (2 s), none closed, on top of
    /// <paramref name="opened"/> opened and <paramref name="closed"/> closed every sample before, during and after it.
    /// </summary>
    private static (JsonElement Shape, string Md) LeakReport(uint opened, uint closed)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5 };
            var h = new History();
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), new ProcessRegistry()) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            var t = World.T0;
            ulong bytes = 50 * (ulong)Settings.MB;
            uint allocs = 1_000_000, frees = 1_000_000;
            void Step(uint opens, uint closes)
            {
                allocs += opens;
                frees += closes;
                h.Add(new Sample(t, new PoolTotals(400_000_000, 300_000_000, 350_000_000, 250_000_000),
                                 [new PoolTagEntry(PoolTag.Parse("File"), 0, 0, 0, allocs, frees, bytes)], []));
                mgr.Process(h, det, det.Evaluate(h), t);
                t = t.AddSeconds(2);
            }
            for (int i = 0; i < 15; i++) Step(opened, closed);
            // Each step is a tenth of the rise, well over the 5% that marks the onset, so the growth starts at the last flat sample.
            for (int i = 0; i < 10; i++) { bytes += 800_000; Step(1_000 + opened, closed); }
            for (int i = 0; i < 20; i++) Step(opened, closed);
            var inc = Assert.Single(mgr.Closed);

            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
            return (json.RootElement.GetProperty("allocationShape").Clone(), File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TheReportShowsTheRatesTheShapeWasJudgedAgainst()
    {
        // A busy File tag (330 opened and 300 closed every 2 s: 9,900 and 9,000 a minute, so the two cannot be swapped)
        // under the leak. On its own ratio (39,900 allocations, 9,000 frees a minute) that is growing with churn; against the
        // rates just before the growth it is the leak shape, and the report says what it was judged against. The numbers are
        // formatted as the report formats them.
        var (shape, md) = LeakReport(opened: 330, closed: 300);
        Assert.Equal("LeakShape", shape.GetProperty("shape").GetString());
        Assert.Equal(9_900, shape.GetProperty("before").GetProperty("allocsPerMin").GetDouble());
        Assert.Equal(9_000, shape.GetProperty("before").GetProperty("freesPerMin").GetDouble());
        Assert.Contains($"{39_900:N0} allocations/min, {9_000:N0} frees/min, against {9_900:N0} and {9_000:N0} a minute just before it: " +
                        "allocations rising, frees flat (leak shape)", md);
    }

    [Fact]
    public void AnIdleBaselineIsLeftOutOfTheReport()
    {
        // The same leak on a tag that was quiet before it: there was nothing to judge it against, so nothing is reported.
        var (shape, md) = LeakReport(opened: 0, closed: 0);
        Assert.Equal("LeakShape", shape.GetProperty("shape").GetString());
        Assert.Equal(JsonValueKind.Null, shape.GetProperty("before").ValueKind);
        Assert.Contains($"{30_000:N0} allocations/min, 0 frees/min: allocations rising, frees flat (leak shape)", md);
    }
}
