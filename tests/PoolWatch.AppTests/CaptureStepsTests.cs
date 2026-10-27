using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;

namespace PoolWatch.AppTests;

/// <summary>
/// Review round 3: the pipeline's capture steps. Which incidents share one trace (and the held-back ones joining it), what
/// the ones left out are told and when, each note at most once per reason, and the governor hearing the end of a trace
/// whatever happens while it is attached. No trace is recorded.
/// </summary>
public class CaptureStepsTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 20, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Now = TimeSpan.FromHours(3);
    private const long MB = 1L << 20;

    private static Incident Inc(string id, string tag, bool open = true) =>
        new() { Id = id, Dir = id, Tag = PoolTag.Parse(tag), Kind = IncidentKind.Growth, OpenedUtc = T0, ClosedUtc = open ? null : T0 };

    private static (Incident, Alert) Opened(Incident inc, long mb) =>
        (inc, new Alert(AlertKind.Growth, inc.Tag, T0, "grew > 200 MB in 10 min", mb * MB, TimeSpan.FromMinutes(10), T0));

    private static CaptureRequest Held(params Incident[] incs) =>
        new(CaptureSource.PoolAlert, T0.AddMinutes(-5), Now - TimeSpan.FromMinutes(5), "earlier") { Context = incs.ToList() };

    [Fact]
    public void TagsThatAlertTogetherShareOneTraceAndHeldIncidentsJoinIt()
    {
        var reff = Inc("reff", "Reff");
        var mmdl = Inc("mmdl", "MmDl");
        var reff2 = Inc("reff-2", "Reff");                 // another incident of a tag already in the trace: fits
        var closed = Inc("old", "File", open: false);      // its incident closed: not joined
        var batch = CaptureSteps.Plan([Opened(reff, 300)], Held(mmdl, reff2, closed), T0, Now, 3)!;
        Assert.Equal(["reff", "mmdl", "reff-2"], batch.Chosen.Select(i => i.Id));
        Assert.Empty(batch.Left);
        Assert.Same(batch.Chosen, batch.Request.Context);
        Assert.Equal(CaptureSource.PoolAlert, batch.Request.Source);
        Assert.Equal(Now, batch.Request.At);
        Assert.Contains("reff, mmdl, reff-2", batch.Request.Why);
        Assert.Equal(2, CaptureSteps.TagsOf(batch.Chosen, 3).Count);
    }

    [Fact]
    public void AtMostThreeTagsTheLargestFirstAndTheOpenedBeforeTheHeld()
    {
        var a = Inc("a", "AAAA"); var b = Inc("b", "BBBB"); var c = Inc("c", "CCCC"); var d = Inc("d", "DDDD");
        var heldOne = Inc("held", "EEEE");
        var batch = CaptureSteps.Plan([Opened(d, 210), Opened(a, 900), Opened(b, 500), Opened(c, 400)], Held(heldOne), T0, Now, 3)!;
        Assert.Equal(["a", "b", "c"], batch.Chosen.Select(i => i.Id));
        Assert.Equal(["d"], batch.Left.Select(i => i.Id)); // the smallest of this tick; the held one is not joined (no room)
        Assert.Null(CaptureSteps.Plan([], Held(heldOne), T0, Now, 3));
    }

    [Fact]
    public void TheLeftOutAreToldAfterTheDecisionAndOnlyWhenATraceIsInPlay()
    {
        var req = new CaptureRequest(CaptureSource.PoolAlert, T0, Now, "x");
        var scope = new CaptureScope(true, false, false);
        Assert.Contains("the trace that started", CaptureSteps.LeftNote(new CaptureDecision(CaptureVerdict.Start, req, scope), 3));
        Assert.Contains("the held-back trace keeps", CaptureSteps.LeftNote(new CaptureDecision(CaptureVerdict.Skipped, req, scope, "busy"), 3));
        Assert.Null(CaptureSteps.LeftNote(new CaptureDecision(CaptureVerdict.Off, req, scope), 3)); // CaptureOnAlert off: nothing to say
    }

    [Fact]
    public void EachNoteAtMostOncePerIncidentAndReason()
    {
        var notes = new CaptureNotes();
        var inc = Inc("a", "Reff");
        Assert.True(notes.First(inc, "capture skipped: at most one automatic capture per 60 min (the last capture ended 20 min ago)"));
        Assert.False(notes.First(inc, "capture skipped: at most one automatic capture per 60 min (the last capture ended 21 min ago)")); // same reason, next tick
        Assert.True(notes.First(inc, "capture skipped: the PC is saturated (CPU 93%, over 85%)"));                                     // another reason
        Assert.True(notes.First(Inc("b", "Reff"), "capture skipped: at most one automatic capture per 60 min (...)"));                   // another incident
        notes.Reset(inc); // a trace started for it: a later episode may be told again
        Assert.True(notes.First(inc, "capture skipped: at most one automatic capture per 60 min (later)"));
    }

    [Fact]
    public void TheGovernorHearsTheEndFirstWhateverHappensWhileAttaching()
    {
        var s = new Settings { CaptureOnAlert = true };
        s.Normalize();
        var clock = TimeSpan.FromHours(1);
        var g = new CaptureGovernor(s, () => clock);
        var a = Inc("a", "Reff");
        var b = Inc("b", "MmDl");
        g.Started(new CaptureRequest(CaptureSource.PoolAlert, T0, clock, "x"));
        clock += TimeSpan.FromMinutes(2);
        var notes = new List<(string, string)>();
        var applied = new List<string>();
        var logged = new List<string>();
        CaptureSteps.Complete(g, [a, b], new CaptureResult { Error = "wpr -stop failed (1)" }, (i, t) => notes.Add((i.Id, t)),
            i => { if (i.Id == "a") throw new InvalidOperationException("disk full"); applied.Add(i.Id); }, logged.Add);

        Assert.Equal(clock, g.LastEnd);                                    // the next automatic capture counts from here
        Assert.Equal(["b"], applied);                                      // a's failure did not stop b
        Assert.Contains(logged, l => l.Contains("disk full"));
        Assert.Equal([("a", "capture failed: wpr -stop failed (1)"), ("b", "capture failed: wpr -stop failed (1)")], notes);
    }
}
