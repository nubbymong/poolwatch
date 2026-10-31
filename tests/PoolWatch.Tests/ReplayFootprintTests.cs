using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// Hours of a busy PC replayed through the registry, the event summarizer, the history, the detector and the incidents,
/// sampled every 20 s: two short commands a second with unique 1 KB command lines, a hundred busy long-lived processes,
/// and three growth incidents an hour on four tags in turn. What PoolWatch holds must stop growing once the six-hour
/// incident retention is full, and stay inside a budget: a structure that is bounded but keeps far more than it shows
/// fails here, on every pull request, without a CI soak.
/// </summary>
[Collection(FootprintCollection.Name)]
public sealed class ReplayFootprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] Tags = ["Reff", "File", "Ntff", "FMfn"];

    [Fact]
    public void HoursOfChurnAndIncidentsHoldAFlatHeapOnceTheRetentionIsFull()
    {
        var w = new World { Interval = TimeSpan.FromSeconds(20) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)], IncidentQuietMinutes = 5 };
        // Re-ranked every 5 minutes, not every 30 s, to keep the test short: an incident keeps only its last ranking.
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry) { RewriteInterval = TimeSpan.FromMinutes(5) };
        var det = new Detector(settings);
        var summarizer = new EventSummarizer();
        var rnd = new Random(11);
        w.AddProcess(4, 0, "System");
        for (int s = 0; s < 6; s++) Start(100 + s, 4, "claude.exe");
        for (int a = 0; a < 100; a++) Start(1000 + a, 4, $"app{a % 37}.exe");
        var bytes = Tags.ToDictionary(t => t, _ => 200L << 20);
        var busy = new HashSet<int>(); // short commands seen in a second sample, doing I/O in it
        int nextPid = 20_000;

        const int PerHour = 180;
        var heap = new List<long>();
        var held = new List<(int incidents, int owners, int ownerMembers, int events)>();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 8 * PerHour; i++)
        {
            foreach (var p in w.Procs.Keys.Where(p => p >= 20_000).ToList())
            {
                if (busy.Remove(p)) w.Io(p, other: 200 + rnd.Next(200));
                else Exit(p);
            }
            // Every 20 minutes the next tag grows for 7.5 minutes; each tag every 80 minutes, so each growth is a new
            // incident. Meanwhile one session runs 16 more busy commands a sample: an owner of hundreds of processes.
            bool growing = i % 60 < 22;
            for (int k = 0; k < (growing ? 56 : 40); k++)
            {
                int pid = nextPid++;
                Start(pid, k < 40 ? 100 + pid % 6 : 100 + i / 60 % 6, (pid % 3) switch { 0 => "bash.exe", 1 => "git.exe", _ => "node.exe" });
                if (k < 4 || k >= 40) busy.Add(pid);
            }
            for (int a = 0; a < 100; a++) w.Io(1000 + a, other: rnd.Next(400));
            if (growing) bytes[Tags[i / 60 % Tags.Length]] += 4L << 20;
            w.Sample(Tags.Select(t => (t, bytes[t])).ToArray());
            var now = w.Now - w.Interval;
            mgr.AddEvents(summarizer.Process(w.Registry.DrainEvents(), now, p => w.Registry.GetLive(p)?.Image));
            mgr.Process(w.History, det, det.Evaluate(w.History), now);
            if ((i + 1) % PerHour == 0)
            {
                heap.Add(GC.GetTotalMemory(forceFullCollection: true) - baseline);
                held.Add(mgr.Held());
            }
        }

        // Closed incidents are kept for six hours: after the sixth hour their count stops rising, and so does the heap
        // (but for the minute history's few hundred KB an hour, until its 25 hours are full).
        string figures = string.Join("; ", heap.Select((b, h) => $"hour {h + 1}: {b >> 10} KB, {held[h].incidents} incidents, {held[h].ownerMembers} owner members"));
        Assert.True(held[5].incidents >= 15 && held[7].incidents <= held[5].incidents, figures);
        Assert.True(heap[7] - heap[5] < 3L << 19, figures); // 1.5 MB
        Assert.True(heap.Max() < 30L << 20, figures); // about 21 MB; 46 MB when an incident kept every suspect

        void Start(int pid, int parent, string image)
        {
            long create = w.AddProcess(pid, parent, image, startIndex: w.Index).CreateTime;
            w.Registry.OnStart(pid, parent, create, image, null, CommandLine(image), null, DateTime.FromFileTimeUtc(create));
        }

        // 2 s after the last sample it was seen in: under half a minute, so a short command (rolled up, kept 15 minutes).
        void Exit(int pid)
        {
            long create = w.Procs[pid].CreateTime;
            w.Exit(pid);
            w.Registry.OnStop(pid, create, 0, w.Now - w.Interval + TimeSpan.FromSeconds(2));
        }

        // A unique script of about 1 KB, as an agent runs them.
        string CommandLine(string image) =>
            $"{image} -c \"cd /d/work/{rnd.NextInt64():x16} && " + string.Concat(Enumerable.Range(0, 24).Select(s => $"echo step{s} {rnd.NextInt64():x16} >> out.txt; ")) + "\"";
    }
}
