using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class PolishTests
{
    [Fact]
    public void IdleProcessExitingAtTheEndIsNotALifecycleSignal()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "churn.exe");
        w.AddProcess(200, 4, "svchost.exe");
        long tag = 20 * Settings.MB;
        for (int i = 0; i < 10; i++) w.Sample(("Ntff", tag));
        int onset = w.Index;
        for (int i = 0; i < 15; i++) { w.Io(100, other: 3000 + i * 10); tag += 900_000; w.Sample(("Ntff", tag)); }
        w.Exit(200); // exits exactly when growth stops, having done nothing
        for (int i = 0; i < 8; i++) { w.Sample(("Ntff", tag)); }
        var end = World.T0 + w.Interval * (onset + 15);
        var ranked = Correlator.Rank(w.History, w.Registry, PoolTag.Parse("Ntff"), new CorrelationWindow(World.T0, World.T0 + w.Interval * onset, end, w.Now));
        Assert.Equal("churn.exe", ranked[0].Image);
        var idle = ranked.SingleOrDefault(s => s.Image == "svchost.exe");
        Assert.True(idle is null || (!idle.Lifecycle.Present && idle.Confidence == Confidence.Weak));
    }

    [Fact]
    public void DetailsAreRetriedWhenTheStartCallbackCouldNotReadThem()
    {
        int calls = 0;
        var r = new ProcessRegistry { Enricher = rec => { if (rec.Pid == 50 && ++calls >= 2) rec.ImagePath = @"C:\tools\Leaker.exe"; } };
        var t0 = new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
        var p = new ProcessSample { Pid = 50, ParentPid = 4, CreateTime = t0.ToFileTimeUtc(), ImageName = "Leaker.exe" };
        r.ObserveSnapshot([new ProcessSample { Pid = 4, ImageName = "System" }], t0);
        r.OnStart(50, 4, p.CreateTime, "Leaker.exe", null, "Leaker.exe leak-handles", null, t0); // first attempt fails
        Assert.Null(r.Get(p.Key)!.ImagePath);
        r.ObserveSnapshot([new ProcessSample { Pid = 4, ImageName = "System" }, p], t0.AddSeconds(10));
        Assert.Equal(@"C:\tools\Leaker.exe", r.Get(p.Key)!.ImagePath);
    }
}

public class MarkdownEscapeTests
{
    [Fact]
    public void UnknownOwnerIsEscapedInMarkdownAndRawInJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string tagTxt = Path.Combine(dir, "pooltag.txt");
            File.WriteAllLines(tagTxt, ["File - <unknown> - File objects"]);
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)] };
            var w = new World();
            var mgr = new PoolWatch.Core.Incidents.IncidentManager(settings, new PoolWatch.Core.Tags.TagKnowledge(tagTxt, null), new FakeHost(Path.Combine(dir, "inc")), w.Registry);
            var det = new PoolWatch.Core.Detection.Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long file = 50 * Settings.MB;
            for (int i = 0; i < 10; i++) { w.Sample(("File", file)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            for (int i = 0; i < 10; i++) { w.Io(100, other: 2000); file += 800_000; w.Sample(("File", file)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            mgr.Finish(w.History, w.Now);
            var inc = mgr.All.Single(i => i.Tag == PoolTag.Parse("File"));
            var md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
            Assert.DoesNotContain("<unknown>", md);
            Assert.Contains("(&lt;unknown> File objects)", md);
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))).RootElement;
            Assert.Contains("(<unknown> File objects)", json.GetProperty("headline").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }
}
