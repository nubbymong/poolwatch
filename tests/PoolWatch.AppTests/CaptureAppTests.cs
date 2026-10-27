using PoolWatch.Analysis;
using PoolWatch.Capture;
using PoolWatch.Core;
using PoolWatch.Core.Incidents;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// C1 (review round 2) in the app layer: the held-back capture starts only when the governor says so, and a capture that
/// throws still reports back. No trace is recorded: the runner's capture step is a test function, and the data root is a
/// TEMP folder (no ACL is touched).
/// </summary>
public sealed class CaptureAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-cap-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly CaptureConditions Quiet = new(false, 24, RunQueue: 5, CpuFraction: 0.3, SinceLastInput: TimeSpan.FromMinutes(1));

    [Fact]
    public void TheHeldBackCaptureStartsOnlyWhenTheGovernorAllowsIt()
    {
        var s = new Settings { CaptureOnAlert = true };
        s.Normalize();
        var clock = TimeSpan.FromHours(1);
        var g = new CaptureGovernor(s, () => clock);
        var first = new CaptureRequest(CaptureSource.PoolAlert, DateTime.UtcNow, clock, "incident A");
        g.Started(first);
        clock += TimeSpan.FromMinutes(1);
        g.Finished();
        Assert.True(g.Decide(new CaptureRequest(CaptureSource.PoolAlert, DateTime.UtcNow, clock, "incident B") { Context = "B" }, Quiet).Kept); // the hourly cap

        int started = 0, dropped = 0;
        void Start(CaptureDecision d) => started++;
        void Drop(CaptureDecision d) => dropped++;

        clock += TimeSpan.FromMinutes(30);
        Assert.Null(PendingCaptures.Tick(g, Quiet, null, Start, Drop));                                        // within the hour: wait
        clock += TimeSpan.FromMinutes(31);
        Assert.Null(PendingCaptures.Tick(g, Quiet with { SinceLastInput = TimeSpan.FromSeconds(2) }, null, Start, Drop)); // typing: wait
        Assert.Null(PendingCaptures.Tick(g, Quiet with { CpuFraction = 0.95 }, null, Start, Drop));           // saturated: wait
        Assert.Equal(0, started);

        var d = PendingCaptures.Tick(g, Quiet, null, Start, Drop);                                             // allowed now
        Assert.NotNull(d);
        Assert.True(d!.Start);
        Assert.Equal("B", d.Request.Context);
        Assert.Equal(1, started);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void AHeldBackCaptureNoLongerWantedIsDroppedNotStarted()
    {
        var s = new Settings { CaptureOnAlert = true };
        s.Normalize();
        var g = new CaptureGovernor(s, () => TimeSpan.FromHours(1));
        g.Decide(new CaptureRequest(CaptureSource.PoolAlert, DateTime.UtcNow, TimeSpan.FromHours(1), "incident B") { Context = "B" }, Quiet with { CaptureRunning = true });
        int started = 0, dropped = 0;
        var d = PendingCaptures.Tick(g, Quiet, r => false /* its incident closed */, _ => started++, _ => dropped++);
        Assert.Equal(CaptureVerdict.Off, d?.Verdict);
        Assert.Equal((0, 1), (started, dropped));
        Assert.Null(PendingCaptures.Tick(g, Quiet, null, _ => started++, _ => dropped++)); // nothing pending any more
    }

    [Fact]
    public void ACaptureThatThrowsStillReportsBackAndEnds()
    {
        // C1 a (review round 2): the done callback (the governor's Finished, the incident's "capture failed") used to be
        // skipped when the capture threw.
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "test.log"));
        var runner = new CaptureRunner(paths, log, null, (_, _, _, _, _, _) => throw new InvalidOperationException("wpr exploded"));
        CaptureResult? got = null;
        using var done = new ManualResetEventSlim();
        Assert.True(runner.TryStart(Path.Combine(paths.DataRoot, "incidents", "x"), [PoolTag.Parse("Reff")], new CaptureScope(true, false, false), 60,
            r => { got = r; done.Set(); }, CancellationToken.None));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("capture failed: wpr exploded", got!.Error);
        Assert.Equal([PoolTag.Parse("Reff")], got.Tags);
        Assert.True(SpinWait.SpinUntil(() => !runner.Busy, TimeSpan.FromSeconds(10)));
        Assert.NotNull(runner.LastEnd);
        Assert.Null(runner.BusySince);
    }

    [Fact]
    public void ACallbackThatThrowsDoesNotLeaveTheRunnerBusy()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "test.log"));
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runner = new CaptureRunner(paths, log, null, (_, tags, _, _, _, _) =>
        {
            running.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new CaptureResult { Tags = tags.ToList() };
        });
        Assert.True(runner.TryStart(Path.Combine(paths.DataRoot, "incidents", "y"), [], new CaptureScope(false, false, true), 60,
            _ => throw new InvalidOperationException("attach failed"), CancellationToken.None));
        Assert.True(running.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(runner.Busy);
        Assert.NotNull(runner.BusySince); // what caps the lag probe's pause (C1 b)
        Assert.False(runner.TryStart(Path.Combine(paths.DataRoot, "incidents", "z"), [], new CaptureScope(false, false, true), 60, _ => { }, CancellationToken.None)); // one at a time
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !runner.Busy, TimeSpan.FromSeconds(10)));
    }
}
