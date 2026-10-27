using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Tests;

public class CaptureTests
{
    [Fact]
    public void TraceShareConfirmsAndReranksSuspects()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "quiet.exe");
        w.AddProcess(200, 4, "busy.exe");
        long tag = 10 * Settings.MB;
        for (int i = 0; i < 10; i++) w.Sample(("Ntff", tag));
        for (int i = 0; i < 20; i++) { w.Io(200, other: 4000); w.Io(100, other: 50); tag += 500_000; w.Sample(("Ntff", tag)); }
        var inc = new Incident { Id = "x", Dir = "x", Tag = PoolTag.Parse("Ntff"), Kind = IncidentKind.Growth };
        inc.Suspects = Correlator.Rank(w.History, w.Registry, inc.Tag, new CorrelationWindow(World.T0, World.T0.AddSeconds(20), null, w.Now));
        Assert.Equal("busy.exe", inc.Suspects[0].Image);

        var capture = new CaptureResult
        {
            PoolEventsParsed = true, StartUtc = w.Now.AddSeconds(-30), EndUtc = w.Now, Tags = [inc.Tag],
            Pool =
            [
                new(100, "quiet.exe", inc.Tag, 9_000_000, 1_000_000, 9000, 1000, [new("ntfs!NtfsCreateFcb", 9_000_000)]),
                new(200, "busy.exe", inc.Tag, 1_000_000, 900_000, 1000, 900, []),
            ],
        };
        CaptureAttribution.Apply(inc, capture);
        var top = inc.Suspects[0];
        Assert.Equal("quiet.exe", top.Image);
        Assert.Equal(1, top.Rank);
        Assert.Equal(Confidence.ConfirmedByTrace, top.Confidence);
        Assert.StartsWith("trace: +7.6 MB net Ntff", top.Evidence[0]);
        Assert.Contains("ntfs!NtfsCreateFcb", top.Evidence[0]);
        Assert.NotEqual(Confidence.ConfirmedByTrace, inc.Suspects.Single(s => s.Image == "busy.exe").Confidence);
    }

    [Fact]
    public void UnrelatedChurnDoesNotHideAMegabyteAllocator()
    {
        // On a CI run, antivirus held 20 MB net File during the trace, the leak 7.2 MB (24%).
        var w = new World();
        w.AddProcess(200, 4, "Leaker.exe");
        long tag = 10 * Settings.MB;
        for (int i = 0; i < 20; i++) { w.Io(200, other: 4000, handles: 500); tag += 500_000; w.Sample(("File", tag)); }
        var inc = new Incident { Id = "x", Dir = "x", Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth };
        inc.Suspects = Correlator.Rank(w.History, w.Registry, inc.Tag, new CorrelationWindow(World.T0, World.T0.AddSeconds(4), null, w.Now));
        var capture = new CaptureResult
        {
            PoolEventsParsed = true, StartUtc = w.Now.AddSeconds(-25), EndUtc = w.Now, Tags = [inc.Tag],
            Pool = [new(300, "MsMpEng.exe", inc.Tag, 21_000_000, 600_000, 49128, 1000, []), new(200, "Leaker.exe", inc.Tag, 7_300_000, 83_000, 17348, 200, []), new(400, "x.exe", inc.Tag, 900_000, 0, 1, 0, [])],
        };
        CaptureAttribution.Apply(inc, capture, tagGrowthDuringTrace: 20_000_000);
        Assert.Equal(Confidence.ConfirmedByTrace, inc.Suspects.Single(s => s.Image == "Leaker.exe").Confidence);
        // At GB scale the floor follows the growth: 7.3 MB of a 1 GB rise during the trace is not a confirmation.
        foreach (var s in inc.Suspects) { s.Confidence = Confidence.Correlated; s.Evidence.RemoveAll(e => e.StartsWith("trace:")); }
        CaptureAttribution.Apply(inc, capture, tagGrowthDuringTrace: 1_000_000_000);
        Assert.NotEqual(Confidence.ConfirmedByTrace, inc.Suspects.Single(s => s.Image == "Leaker.exe").Confidence);
    }

    [Fact]
    public void UnparsedTraceChangesNothing()
    {
        var inc = new Incident { Id = "x", Dir = "x", Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth };
        CaptureAttribution.Apply(inc, new CaptureResult { PoolEventsParsed = false, Error = "wpr -start failed" });
        Assert.NotNull(inc.Capture);
        Assert.Empty(inc.Suspects);
    }
}

public class SuppressionTests
{
    [Fact]
    public void SuppressedTagRaisesNoIncident()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-sup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
            var w = new World();
            var mgr = new IncidentManager(settings, new PoolWatch.Core.Tags.TagKnowledge(null, null), new FakeHost(dir), w.Registry);
            var det = new PoolWatch.Core.Detection.Detector(settings);
            mgr.Suppress(PoolTag.Parse("EtwB"), World.T0.AddMinutes(10));
            long v = 10 * Settings.MB;
            for (int i = 0; i < 30; i++) { v += 1_000_000; w.Sample(("EtwB", v)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            for (int i = 0; i < 5; i++) { v -= 5_000_000; w.Sample(("EtwB", v)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            Assert.Empty(mgr.All);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
