using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// Who a release (a sharp drop) names as busy. I/O counters are read only at each sample, and an exit does not report
/// them, so a process that frees the memory (deletes a file tree) and exits between two samples never shows that last
/// burst. Such a process must still be named, marked as exited, when it did I/O in the drop's window.
/// </summary>
public sealed class ReleaseAttributionTests : IDisposable
{
    private const int Leaker = 100, Busy = 200, Early = 300;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-release-" + Guid.NewGuid().ToString("N"));
    private readonly World _w = new();
    private readonly IncidentManager _mgr;
    private readonly Detector _det;
    private long _file = 50 * Settings.MB;

    public ReleaseAttributionTests()
    {
        var settings = new Settings
        {
            SampleIntervalSeconds = 2, GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 0.5,
            ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1,
        };
        _mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), _w.Registry) { RewriteInterval = TimeSpan.Zero };
        _det = new Detector(settings);
        _w.AddProcess(4, 0, "System");
        _w.AddProcess(Leaker, 4, "leaker.exe");
        _w.AddProcess(Busy, 4, "busy.exe");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private void Step()
    {
        _w.Io(Busy, other: 300); // something else on the PC is always busy
        _w.Sample(("File", _file));
        _mgr.Process(_w.History, _det, _det.Evaluate(_w.History), _w.Now - _w.Interval);
    }

    /// <summary>The CI "files" scenario: create 40 s, hold 30 s idle, then delete and exit at once.</summary>
    private void CreateThenHold(int holdSamples = 15)
    {
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 20; i++) { _w.Io(Leaker, other: 2000); _file += 800_000; Step(); }
        // Held: the leaker is idle. The tag still creeps up a little (other activity), so the window's maximum is the
        // last sample before the drop, as on a real PC.
        for (int i = 0; i < holdSamples; i++) { _file += 10_000; Step(); }
    }

    private ReleaseRecord Release()
    {
        var inc = _mgr.Open.Concat(_mgr.Closed).Where(i => i.Release is not null).OrderByDescending(i => i.Release!.Dropped).FirstOrDefault();
        Assert.True(inc is not null, "no release was recorded");
        return inc!.Release!;
    }

    private static string? Entry(ReleaseRecord r, string image) => r.ActiveProcesses.FirstOrDefault(t => t.StartsWith(image + " ", StringComparison.Ordinal));

    [Fact]
    public void AProcessThatDeletesAndExitsBetweenTwoSamplesIsNamedAsExited()
    {
        CreateThenHold();
        var created = _w.Procs[Leaker].Key.CreateTimeUtc;
        // Deletes the tree and exits after the last sample; the ETW stop reports the exit (no I/O counts).
        _w.Exit(Leaker);
        _w.Registry.OnStop(Leaker, 0, 0, _w.Now - TimeSpan.FromSeconds(0.5)); // as the kernel session reports it (by PID)
        // The PID is reused at once by a new process: it must not be mistaken for the one that exited.
        _w.AddProcess(Leaker, 4, "newcomer.exe", startIndex: _w.Index);
        _file -= 16 * Settings.MB;
        Step();

        var r = Release();
        string? e = Entry(r, "leaker.exe");
        Assert.True(e is not null, "leaker.exe missing from: " + string.Join(" | ", r.ActiveProcesses));
        Assert.Contains($"(PID {Leaker}, started {Fmt.Clock(created)})", e);
        Assert.Contains("exited", e);
        Assert.Contains("I/O after its last sample is not seen", e);
        Assert.DoesNotContain(r.ActiveProcesses, t => t.StartsWith("newcomer.exe", StringComparison.Ordinal) && t.Contains("exited"));
        // A process still running is reported as before: counted from the sample before the drop, not marked.
        string? busy = Entry(r, "busy.exe");
        Assert.True(busy is not null && !busy.Contains("exited"), busy);
    }

    [Fact]
    public void AProcessThatExitedOneSampleBeforeTheDropWasSeenIsNamedAsExited()
    {
        CreateThenHold();
        // Exits (after deleting) one sample before the drop shows: the kernel frees the memory a little later.
        _w.Exit(Leaker);
        _file += 10_000;
        Step();
        _file -= 16 * Settings.MB;
        Step();

        var r = Release();
        string? e = Entry(r, "leaker.exe");
        Assert.True(e is not null, "leaker.exe missing from: " + string.Join(" | ", r.ActiveProcesses));
        Assert.Contains("exited", e);
    }

    [Fact]
    public void AProcessThatExitedLongBeforeTheDropIsNotNamed()
    {
        _w.AddProcess(Early, 4, "early.exe");
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 20; i++) { _w.Io(Leaker, other: 2000); _w.Io(Early, other: 2000); _file += 800_000; Step(); }
        _w.Exit(Early); // gone 15 samples (30 s) before the drop: it cannot have freed this memory
        for (int i = 0; i < 15; i++) { _file += 10_000; Step(); }
        _w.Exit(Leaker);
        _file -= 16 * Settings.MB;
        Step();

        var r = Release();
        Assert.NotNull(Entry(r, "leaker.exe"));
        Assert.Null(Entry(r, "early.exe"));
    }

    [Fact]
    public void AnExitedProcessWithNoIoInTheDropsWindowIsNotNamed()
    {
        // Held longer than the release window (1 min): no I/O of the leaker is left in the window.
        CreateThenHold(holdSamples: 35);
        _w.Exit(Leaker);
        _file -= 16 * Settings.MB;
        Step();

        var r = Release();
        Assert.Null(Entry(r, "leaker.exe"));
    }
}
