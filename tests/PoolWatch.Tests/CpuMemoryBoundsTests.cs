using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// What bounds PoolWatch's CPU and memory under agentic load, each without changing what the windows, reports and exports
/// show: the correlator's ranks of a counter that is mostly 0, the history's packed points, a fork copy's command line, the
/// registry's compact and named records, the incidents' rankings, onsets and count.
/// </summary>
public sealed class CpuMemoryBoundsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-bounds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A counter that is 0 in most samples ranks its zeros as one block and sorts the rest: Stats' result to the bit.</summary>
    [Fact]
    public void ACounterThatIsMostlyZeroRanksAsStatsDoesToTheBit()
    {
        var rnd = new Random(11);
        foreach (int n in new[] { 4, 5, 9, 40, 180, 419 })
        {
            var tag = Enumerable.Range(0, n).Select(_ => (double)(rnd.Next(7) - 2) * 1048576.0).ToArray();
            var ranks = new Correlator.TagRanks(tag);
            for (int trial = 0; trial < 20; trial++)
            {
                // 0 to 6 samples with I/O (ties among them too), the rest 0: a short command seen in a sample or two.
                var d = new double[n];
                for (int k = rnd.Next(7); k > 0; k--) d[rnd.Next(n)] = rnd.Next(3) * 100.0;
                for (int lag = 0; lag <= Correlator.MaxLag; lag++)
                {
                    double expected = lag == 0 ? Stats.Spearman(tag, d) : Stats.SpearmanLagged(tag, d, lag);
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(ranks.Spearman(d, lag)));
                }
            }
        }
    }

    /// <summary>
    /// The history keeps a process's points in half the space and gives each back exactly: across a counter that fell, an
    /// I/O rise of more than 32 bits and a quota of 4 GB (the track turns to plain points then), and as the raw history drops
    /// its oldest samples.
    /// </summary>
    [Fact]
    public void TheHistoryGivesBackEveryPointExactlyAsItWasAdded()
    {
        var h = new History(rawSpan: TimeSpan.FromMinutes(5), maxRawSamples: 100);
        var expected = new Dictionary<int, List<ProcPoint>>();
        var key = new Dictionary<int, ProcessKey>();
        for (int i = 0; i < 400; i++)
        {
            var procs = new List<ProcessSample>();
            for (int p = 1; p <= 3; p++)
            {
                long read = 1_000_000L * i + p, other = 5L * i;
                ulong quota = 4096UL * (ulong)i;
                if (p == 1 && i == 200) read = 0; // below its first point (never for a live process, but kept exactly)
                if (p == 2 && i > 250) read = (long)i * 30_000_000; // more than 4 billion above its first point
                if (p == 3 && i >= 300) quota = 5UL << 30; // 5 GB
                var s = new ProcessSample { Pid = 100 + p, CreateTime = 1000 + p, ImageName = "x.exe", HandleCount = i + p, ReadOperationCount = read, WriteOperationCount = 7L * i, OtherOperationCount = other, QuotaPagedPoolUsage = quota, QuotaNonPagedPoolUsage = 2 * quota };
                procs.Add(s);
                key[p] = s.Key;
                (expected.TryGetValue(p, out var l) ? l : expected[p] = []).Add(new ProcPoint(s.HandleCount, read, 7L * i, other, (long)quota, (long)(2 * quota)));
            }
            h.Add(new Sample(T0.AddSeconds(10 * i), default, [], procs) { TickMs = 10_000L * i });
        }
        Assert.True(h.Count <= 100);
        for (int p = 1; p <= 3; p++)
        {
            var pts = h.Points(key[p]);
            var want = expected[p].Skip(400 - h.Count).ToList();
            Assert.Equal(want.Count, pts.Last - pts.First + 1);
            for (int i = pts.First; i <= pts.Last; i++) Assert.Equal(want[i - pts.First], pts[i]);
            Assert.Equal(want, h.ProcessSeries(key[p]).Select(x => x!.Value));
        }
    }

    private static string Script(int i) => "\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" -c \"" + string.Concat(Enumerable.Range(0, 40).Select(k => $"echo {i}-{k} >> out.txt; ")) + "\"";

    /// <summary>A Git Bash fork copy (the shell's image and exact command line) holds its parent's command line, not one of its own.</summary>
    [Fact]
    public void AForkCopyHoldsItsParentsCommandLine()
    {
        var reg = new ProcessRegistry(clock: () => At(T0));
        string cl = Script(1);
        var bash = reg.OnStart(500, 4, T0.ToFileTimeUtc(), "bash.exe", null, cl, null, T0);
        var copy = reg.OnStart(501, 500, T0.ToFileTimeUtc() + 1, "bash.exe", null, new string(cl.AsSpan()), null, T0);
        var other = reg.OnStart(502, 500, T0.ToFileTimeUtc() + 2, "bash.exe", null, Script(2), null, T0);
        Assert.True(ReferenceEquals(bash.CommandLine, copy.CommandLine));
        Assert.Equal(Script(2), other.CommandLine);
        Assert.False(ReferenceEquals(bash.CommandLine, other.CommandLine));
        Assert.Equal(cl, reg.DrainEvents().Single(e => e.Pid == 501).CommandLine);
    }

    /// <summary>
    /// Inside an incident's window, with no trace recording, a short command past the newest 3,000 stays as a compact
    /// record only when a ranking can name it: a snapshot listed it, or it is the ancestor (a fork copy, its shell) of one
    /// that was. The fork copies' sub-second tools, never sampled, are in no ranking and go.
    /// </summary>
    [Fact]
    public void WithoutATraceOnlyTheProcessesARankingCanNameStayCompact()
    {
        var now = T0;
        var reg = new ProcessRegistry(clock: () => At(now)) { KeepShortLived = TimeSpan.FromMinutes(15), ProtectSinceMs = At(T0.AddMinutes(-10)).Ms };
        long ft = T0.ToFileTimeUtc();
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
        var session = reg.OnStart(100, 4, ft - 1000, "claude.exe", null, "claude", null, T0);
        // The command the snapshot saw: a shell, its fork copy, and the program under the copy, all seen running.
        reg.OnStart(200, 100, ft + 1, "bash.exe", null, Script(0), null, T0);
        reg.OnStart(201, 200, ft + 2, "bash.exe", null, Script(0), null, T0);
        reg.OnStart(202, 201, ft + 3, "node.exe", null, "node build.js", null, T0);
        now = T0.AddSeconds(5);
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 100, ParentPid = 4, CreateTime = ft - 1000, ImageName = "claude.exe" },
            new() { Pid = 202, ParentPid = 201, CreateTime = ft + 3, ImageName = "node.exe" }], T0.AddSeconds(5));
        now = T0.AddSeconds(8); // the registry reads each exit onto its clock as it arrives
        foreach (int pid in new[] { 202, 201, 200 }) reg.OnStop(pid, ft + pid - 199, 0, T0.AddSeconds(8));
        // Then 3,500 sub-second tools under the session's shells, never in a snapshot.
        for (int i = 0; i < ProcessRegistry.MaxProtectedShortLived + 500; i++)
        {
            var t = T0.AddSeconds(10).AddMilliseconds(i * 10);
            now = t.AddMilliseconds(3);
            reg.OnStart(10_000 + i, 100, t.ToFileTimeUtc(), "sed.exe", null, "sed -n 1p", null, t);
            reg.OnStop(10_000 + i, t.ToFileTimeUtc(), 0, t.AddMilliseconds(3));
        }
        now = T0.AddMinutes(5);
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 100, ParentPid = 4, CreateTime = ft - 1000, ImageName = "claude.exe" }], now);
        Assert.Equal(["claude.exe", "bash.exe", "bash.exe", "node.exe"], reg.Ancestry(new ProcessKey(202, ft + 3)).Select(r => r.Image));
        Assert.True(reg.Get(new ProcessKey(10_000, T0.AddSeconds(10).ToFileTimeUtc())) is null, $"count {reg.Count} compact {reg.CompactCount} named {reg.NamedCount}"); // a tool past the newest 3,000: gone
        Assert.Equal(3, reg.CompactCount); // the node and its two shells
        Assert.NotNull(reg.Get(new ProcessKey(10_000 + ProcessRegistry.MaxProtectedShortLived + 499, T0.AddSeconds(10).AddMilliseconds((ProcessRegistry.MaxProtectedShortLived + 499) * 10).ToFileTimeUtc())));
        Assert.Equal(Core.Identity.OwnerKind.ClaudeSession, OwnerKindOf(reg, new ProcessKey(202, ft + 3)));
        GC.KeepAlive(session);
    }

    private static Core.Identity.OwnerKind OwnerKindOf(ProcessRegistry reg, ProcessKey key) => Core.Identity.ProcessIdentities.OwnerOf(reg.Ancestry(key)).Kind;

    /// <summary>
    /// A process that ran 30 s or more is kept for the day, for the since-boot report's names: past the raw history and its
    /// margin as a named record (no longer counted among the full records), with its name, services, label and a short
    /// command line, without an agent's long script (events.csv and the reports have it).
    /// </summary>
    [Fact]
    public void AnExitedProcessPastTheRawHistoryKeepsItsNameButNotItsScript()
    {
        var now = T0;
        var reg = new ProcessRegistry(clock: () => At(now));
        long ft = T0.ToFileTimeUtc();
        var svc = reg.OnStart(300, 4, ft, "svchost.exe", null, "svchost -k netsvcs", null, T0);
        var long1 = reg.OnStart(301, 4, ft + 1, "bash.exe", null, Script(3), null, T0);
        svc.Services = ["Winmgmt"];
        long1.Label = "build";
        for (int i = 0; i < 300; i++) reg.OnStart(1000 + i, 4, ft + 10 + i, "app.exe", null, "app", null, T0); // past the 256 a prune needs
        now = T0.AddMinutes(1);
        reg.OnStop(300, ft, 0, now);
        reg.OnStop(301, ft + 1, 0, now);
        string svcName = svc.DisplayName, bashName = long1.DisplayName;
        now = T0.AddMinutes(1 + 80);
        reg.ObserveSnapshot([], now); // 80 minutes on: still full
        Assert.Equal(Script(3), reg.Get(long1.Key)!.CommandLine);
        now = T0.AddMinutes(1 + 95);
        int before = reg.Count;
        reg.ObserveSnapshot([], now); // 95 minutes on: named
        Assert.Equal(2, reg.NamedCount);
        Assert.Equal(before - 2, reg.Count);
        Assert.Equal(svcName, reg.Get(svc.Key)!.DisplayName);
        Assert.Equal("svchost -k netsvcs", reg.Get(svc.Key)!.CommandLine);
        Assert.Equal(bashName, reg.Get(long1.Key)!.DisplayName);
        Assert.Null(reg.Get(long1.Key)!.CommandLine);
        Assert.Equal(now.AddMinutes(-95), reg.Get(long1.Key)!.ExitUtc);
        now = T0.AddDays(1).AddMinutes(10);
        reg.ObserveSnapshot([], now); // the day is over
        Assert.Null(reg.Get(svc.Key));
    }

    /// <summary>
    /// Ten growth incidents open for an hour on a busy PC, with the default write budget: an incident's suspects are ranked
    /// when it opens, before every rewrite (each report holds the ranking of the moment it is written) and at most every
    /// 2 minutes while the budget has room, at least every 10 for the cause shown from memory; not every 30 s.
    /// </summary>
    [Fact]
    public void SuspectsAreRankedOnlyWhenARewriteCanFollowAndEveryReportHoldsTheRankingOfItsMoment()
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < 20; p++) w.AddProcess(100 + p, 4, "node.exe");
        string[] tags = Enumerable.Range(0, 10).Select(i => $"T{i:D3}").ToArray();
        var bytes = tags.ToDictionary(t => t, _ => 100L << 20);
        var rnd = new Random(5);
        long writesChecked = 0, lastMs = 0;
        for (int i = 0; i < 400; i++)
        {
            for (int p = 0; p < 20; p++) w.Io(100 + p, other: 50 + rnd.Next(500));
            if (i >= 30) foreach (var t in tags) bytes[t] += 1L << 20;
            w.Sample(tags.Select(t => (t, bytes[t])).ToArray());
            var now = At(w.Now - w.Interval);
            mgr.Process(w.History, det, det.Evaluate(w.History), now);
            foreach (var inc in mgr.Open.Where(inc => inc.LastWriteMs == now.Ms))
            {
                Assert.Equal(now.Ms, inc.SuspectsRankMs); // written now: ranked now
                writesChecked++;
            }
            foreach (var inc in mgr.Open) Assert.True(now.Ms - inc.SuspectsRankMs!.Value <= (long)mgr.SuspectsRefreshInterval.TotalMilliseconds + 30_000);
            lastMs = now.Ms;
        }
        Assert.Equal(10, mgr.Open.Count);
        Assert.True(writesChecked >= 10);
        // Every 30 s would be 10 rankings a 30 s; at most one an incident every 2 minutes while the budget has room, and the
        // rewrites' own (each ranked just before), are far fewer.
        double minutes = (lastMs - mgr.Open.Min(inc => inc.OpenedMs)) / 60_000.0;
        Assert.InRange(mgr.SuspectRankings, writesChecked, 10 + 10 * (long)(minutes / 2 + 1) + writesChecked);
        Assert.True(mgr.SuspectRankings < 10 * minutes * 2 / 3, $"{mgr.SuspectRankings} rankings in {minutes:0} minutes");
    }

    /// <summary>
    /// An incident open longer than the raw history (70 minutes) keeps its onset and start level: they are not moved to the
    /// history's first sample, so its growth does not shrink as the history moves on.
    /// </summary>
    [Fact]
    public void AnIncidentOpenLongerThanTheRawHistoryKeepsItsOnsetAndItsGrowth()
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "crawler.exe");
        long tag = 100L << 20;
        long? onsetUncovered = null;
        for (int i = 0; i < 650; i++) // 108 minutes, growing 6 MB a minute from minute 5
        {
            w.Io(100, other: 1000);
            if (i >= 30) tag += 1L << 20;
            w.Sample(("Reff", tag));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
            if (onsetUncovered is null && mgr.Open.SingleOrDefault() is { } open && w.History.Ms[0] > open.OnsetMs) onsetUncovered = open.OnsetMs; // the first sample the history no longer reaches back to it
        }
        var inc = Assert.Single(mgr.Open);
        Assert.True(w.History.Ms[0] > inc.OnsetMs, "the history no longer reaches back to the onset");
        Assert.True(onsetUncovered == inc.OnsetMs, $"onset when the history passed it {onsetUncovered}, now {inc.OnsetMs}, history from {w.History.Ms[0]}");
        Assert.InRange(inc.StartBytes, 100L << 20, 140L << 20); // where the growth began, not the 300 MB of the history's first sample
        Assert.InRange(inc.Growth, 580L << 20, 620L << 20); // the 620 samples of 1 MB from the onset, not the 420 the history still holds
    }

    /// <summary>At most 20 closed incidents stay in memory; the oldest closed leaves first, its folder stays.</summary>
    [Fact]
    public void AtMostTwentyClosedIncidentsStayInMemory()
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(1, 5 * Settings.MB)], IncidentQuietMinutes = 1 };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "crawler.exe");
        var tags = Enumerable.Range(0, 25).Select(i => $"C{i:D3}").ToArray();
        var bytes = tags.ToDictionary(t => t, _ => 100L << 20);
        for (int i = 0; i < 25 * 18 + 30; i++)
        {
            w.Io(100, other: 1000);
            int growing = i / 18; // each tag grows for 1 minute, then the next
            if (growing < tags.Length && i % 18 < 6) bytes[tags[growing]] += 2L << 20;
            w.Sample(tags.Select(t => (t, bytes[t])).ToArray());
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
        }
        Assert.Equal(25, mgr.CreatedCount);
        Assert.Empty(mgr.Open);
        Assert.Equal(IncidentManager.MaxClosed, mgr.Closed.Count);
        Assert.Equal(tags.Skip(5).Select(PoolTag.Parse), mgr.Closed.Select(c => c.Tag)); // the first five left memory
        Assert.Equal(25, Directory.GetDirectories(_dir).Length); // their folders stay
    }

    /// <summary>
    /// The named records are at most <see cref="ProcessRegistry.MaxNamed"/> whenever a prune has run, also when the full
    /// registry went over its cap in that pass and its eviction named more of them (profile.json's namedRecords, which the
    /// CI soak holds to 10,000): the first named go first, the ones just named stay.
    /// </summary>
    [Fact]
    public void TheNamedRecordsStayWithinTheirCeilingAlsoWhenTheEvictionNamesMore()
    {
        var now = T0;
        var reg = new ProcessRegistry(maxRecords: 2_000, clock: () => At(now));
        long ft = T0.ToFileTimeUtc();
        // A day's worth of names: processes that ran a minute, named once past the raw history.
        for (int i = 0; i < ProcessRegistry.MaxNamed; i++) reg.OnStart(100_000 + i, 4, ft + i, "build.exe", null, "build", null, T0);
        now = T0.AddMinutes(1);
        for (int i = 0; i < ProcessRegistry.MaxNamed; i++) reg.OnStop(100_000 + i, ft + i, 0, now);
        now = T0.AddMinutes(1 + 95);
        reg.ObserveSnapshot([], now);
        Assert.Equal(ProcessRegistry.MaxNamed, reg.NamedCount);
        // Then more long-lived processes than the registry holds, exited a minute ago: the eviction names the earliest 900.
        var t1 = now;
        long ft1 = t1.ToFileTimeUtc();
        for (int i = 0; i < 2_500; i++) reg.OnStart(300_000 + i, 4, ft1 + i, "test.exe", null, "test", null, t1);
        for (int i = 0; i < 2_500; i++)
        {
            now = t1.AddMinutes(1).AddMilliseconds(i);
            reg.OnStop(300_000 + i, ft1 + i, 0, now);
        }
        now = t1.AddMinutes(2);
        reg.ObserveSnapshot([], now);
        Assert.True(reg.NamedCount <= ProcessRegistry.MaxNamed, $"{reg.NamedCount:N0} named records, past the {ProcessRegistry.MaxNamed:N0} they may be");
        Assert.True(reg.Count <= 2_000, $"{reg.Count} full records");
        Assert.Null(reg.Get(new ProcessKey(100_000, ft)));         // the first named went first
        Assert.NotNull(reg.Get(new ProcessKey(300_000, ft1)));     // the eviction's first, just named, stays
    }

    /// <summary>
    /// A process that exited hours before a child was created is not that child's parent, also once its record is a named
    /// one (a long-lived process past the raw history): a storm is never credited to a process long gone.
    /// </summary>
    [Fact]
    public void AProcessThatExitedHoursBeforeAChildIsNotItsParentAsANamedRecord()
    {
        var now = T0;
        var reg = new ProcessRegistry(clock: () => At(now));
        long ft = T0.ToFileTimeUtc();
        reg.OnStart(700, 4, ft, "node.exe", null, "node server.js", null, T0);
        for (int i = 0; i < 300; i++) reg.OnStart(1000 + i, 4, ft + 10 + i, "app.exe", null, "app", null, T0); // past the 256 a prune needs
        now = T0.AddMinutes(1);
        reg.OnStop(700, ft, 0, now); // ran a minute: kept for the day
        now = T0.AddMinutes(1 + 95);
        reg.ObserveSnapshot([], now); // past the raw history: named
        Assert.Equal(1, reg.NamedCount);
        Assert.NotNull(reg.Get(new ProcessKey(700, ft)));
        Assert.Null(reg.ParentKeyOf(700, T0.AddMinutes(100).ToFileTimeUtc()));
        Assert.Equal(new ProcessKey(700, ft), reg.ParentKeyOf(700, T0.AddSeconds(30).ToFileTimeUtc())); // one it was alive for
    }

    /// <summary>
    /// A busy process's track stays packed for good: its counters are measured from the oldest point held, not from the first
    /// it ever had (12 billion operations in 600 samples, a 100-sample history), every point given back exactly; an index
    /// outside its points is refused, not read from a stale slot.
    /// </summary>
    [Fact]
    public void ABusyProcessStaysPackedAndAnIndexOutsideItsPointsIsRefused()
    {
        var h = new History(rawSpan: TimeSpan.FromMinutes(15), maxRawSamples: 100);
        var expected = new List<ProcPoint>();
        ProcessKey key = default;
        for (int i = 0; i < 600; i++)
        {
            var s = new ProcessSample { Pid = 9, CreateTime = 99, ImageName = "build.exe", HandleCount = 300 + i % 7, ReadOperationCount = 20_000_000L * i, WriteOperationCount = 3_000_000L * i, OtherOperationCount = 7_000_000L * i + i % 3 };
            key = s.Key;
            expected.Add(new ProcPoint(s.HandleCount, s.ReadOperationCount, s.WriteOperationCount, s.OtherOperationCount, 0, 0));
            h.Add(new Sample(T0.AddSeconds(10 * i), default, [], [s]) { TickMs = 10_000L * i });
        }
        var pts = h.Points(key);
        Assert.True(h.TrackOf(key)!.IsPacked, "the track turned into plain points");
        var want = expected.Skip(600 - h.Count).ToList();
        for (int i = pts.First; i <= pts.Last; i++) Assert.Equal(want[i - pts.First], pts[i]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pts[pts.Last + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pts[pts.First - 1]);
    }

    /// <summary>
    /// A point equal to the one before takes no space: an idle process holds one run for the whole history, one that changes
    /// every 10 samples one run each time, and every point of each comes back exactly, read in order or not, as the oldest go.
    /// </summary>
    [Fact]
    public void AnIdleProcessHoldsOneRunAndEveryPointComesBack()
    {
        var h = new History(rawSpan: TimeSpan.FromMinutes(15), maxRawSamples: 100);
        var expected = new Dictionary<int, List<ProcPoint>>();
        var keys = new Dictionary<int, ProcessKey>();
        var rnd = new Random(12);
        for (int i = 0; i < 700; i++)
        {
            var procs = new List<ProcessSample>();
            for (int p = 1; p <= 3; p++)
            {
                // 1 idle; 2 a new value every 10 samples (handles and I/O); 3 a new value most samples, sometimes the same twice
                long other = p switch { 1 => 500, 2 => 1_000L * (i / 10), _ => rnd.Next(4) == 0 ? 7 : 1_000L * i };
                var s = new ProcessSample { Pid = 10 + p, CreateTime = 50 + p, ImageName = "x.exe", HandleCount = p == 2 ? 100 + i / 10 : 100, OtherOperationCount = other, QuotaPagedPoolUsage = 4096 };
                keys[p] = s.Key;
                procs.Add(s);
                (expected.TryGetValue(p, out var l) ? l : expected[p] = []).Add(new ProcPoint(s.HandleCount, 0, 0, other, 4096, 0));
            }
            h.Add(new Sample(T0.AddSeconds(10 * i), default, [], procs) { TickMs = 10_000L * i });
        }
        Assert.Equal(1, h.TrackOf(keys[1])!.Runs);
        Assert.InRange(h.TrackOf(keys[2])!.Runs, h.Count / 10, h.Count / 10 + 2);
        for (int p = 1; p <= 3; p++)
        {
            var pts = h.Points(keys[p]);
            var want = expected[p].Skip(700 - h.Count).ToList();
            Assert.Equal(want, h.ProcessSeries(keys[p]).Select(x => x!.Value)); // in order
            foreach (int i in Enumerable.Range(pts.First, h.Count).OrderBy(_ => rnd.Next())) Assert.Equal(want[i - pts.First], pts[i]); // in any order
        }
    }

    /// <summary>
    /// A handle scan answers as the dictionaries it came in (each type, all types, a type or a process it has not); what it
    /// holds is measured on its own (<see cref="HandleScanFootprint"/>).
    /// </summary>
    [Fact]
    public void AHandleScanAnswersAsTheDictionariesItCameIn()
    {
        string[] types = ["File", "Event", "Key", "Section", "Thread", "Mutant", "Semaphore", "ALPC Port", "WaitCompletionPacket", "IoCompletion", "TpWorkerFactory", "Directory", "Token", "Desktop", "Timer", "EtwRegistration"];
        var rnd = new Random(3);
        Dictionary<ProcessKey, Dictionary<string, int>> Scan(int procs) =>
            Enumerable.Range(0, procs).ToDictionary(p => new ProcessKey(4 * p + 8, 1000 + p),
                p => types.Where(_ => rnd.Next(16) != 0).Take(15).ToDictionary(t => new string(t.AsSpan()), _ => rnd.Next(1, 3000)));
        var check = Scan(200);
        var scan = new TypedHandles(T0, check) { Ms = 1 };
        foreach (var (k, d) in check)
        {
            Assert.True(scan.Has(k));
            foreach (var t in types) Assert.Equal(d.GetValueOrDefault(t), scan.Count(k, t));
            Assert.Equal(d.Values.Sum(), scan.Count(k, "*"));
            Assert.Equal(0, scan.Count(k, "NoSuchType"));
        }
        Assert.False(scan.Has(new ProcessKey(5, 1)));
        Assert.Equal(0, scan.Count(new ProcessKey(5, 1), "File"));
        Assert.Equal(0, scan.Count(new ProcessKey(5, 1), "*"));
        Assert.Equal(200, scan.ProcessCount);
    }

    /// <summary>
    /// The minutes older than 2 hours have their processes summed by the hour (the busiest 10 and the hour's total): the
    /// since-boot report over a day reads exactly what it read from the minutes themselves (each hour's top processes, their
    /// shares, their bytes, ties in the same order), from a fraction of the entries.
    /// </summary>
    [Fact]
    public void TheSinceBootReportReadsTheSummedHoursAsItReadTheMinutes()
    {
        var reg = new ProcessRegistry(clock: () => At(T0));
        var summed = new History(rawSpan: TimeSpan.FromMinutes(5), maxRawSamples: 100) { NameOf = k => reg.Get(k)?.DisplayName };
        var whole = new History(rawSpan: TimeSpan.FromMinutes(5), maxRawSamples: 100) { MinuteDetailSpan = TimeSpan.FromHours(30) };
        var rnd = new Random(8);
        var live = new Dictionary<int, ProcessSample>();
        long reff = 300L << 20;
        var tag = PoolTag.Parse("Reff");
        for (int i = 0; i < 24 * 360 + 120; i++)
        {
            // About 40 processes at a time, a few new ones a minute, each doing I/O in some samples (ties among them).
            if (i % 6 == 0)
            {
                int pid = 1000 + i;
                live[pid] = new ProcessSample { Pid = pid, ParentPid = 4, CreateTime = T0.AddSeconds(10 * i).ToFileTimeUtc(), ImageName = $"p{pid}.exe" };
                reg.OnStart(pid, 4, live[pid].CreateTime, live[pid].ImageName, null, null, null, T0);
            }
            foreach (var pid in live.Keys.ToList())
            {
                if (live.Count > 40 && rnd.Next(30) == 0) { live.Remove(pid); continue; }
                if (rnd.Next(3) == 0) live[pid] = live[pid] with { OtherOperationCount = live[pid].OtherOperationCount + 10 * rnd.Next(1, 4) };
            }
            reff += rnd.Next(-1 << 18, 1 << 20);
            var s = new Sample(T0.AddSeconds(10 * i), default, [new PoolTagEntry(tag, 0, 0, 0, 1, 0, (ulong)reff)], live.Values.ToList()) { TickMs = 10_000L * i };
            summed.Add(s);
            whole.Add(s);
        }
        var knowledge = new TagKnowledge(null, null);
        var a = SinceBootReport.Build(summed, reg, knowledge);
        var b = SinceBootReport.Build(whole, reg, knowledge);
        var hours = Assert.Single(b).Hours;
        Assert.True(hours.Count >= 24 && hours.Count(x => x.Attribution.Count == 3) >= 10, $"{hours.Count} hours");
        Assert.Equal(Text(b), Text(a));
        Assert.True(summed.MinuteProcessEntries * 5 < whole.MinuteProcessEntries, $"{summed.MinuteProcessEntries} entries against {whole.MinuteProcessEntries}");
        static string Text(List<SinceBootTag> tags) => string.Join("\n", tags.SelectMany(t => t.Hours.Select(x =>
            $"{x.HourUtc:O} {x.Growth} " + string.Join("; ", x.Attribution.Select(at => $"{at.Process} {at.Pid} {BitConverter.DoubleToInt64Bits(at.Share)} {at.Bytes}")))));
    }

    /// <summary>A named record's services are its own: changing the list a lookup gave, or the live record's, changes nothing it holds.</summary>
    [Fact]
    public void ANamedRecordsServicesAreItsOwn()
    {
        var now = T0;
        var reg = new ProcessRegistry(clock: () => At(now));
        long ft = T0.ToFileTimeUtc();
        var svc = reg.OnStart(300, 4, ft, "svchost.exe", null, "svchost -k netsvcs", null, T0);
        svc.Services = ["Winmgmt"];
        for (int i = 0; i < 300; i++) reg.OnStart(1000 + i, 4, ft + 10 + i, "app.exe", null, "app", null, T0);
        now = T0.AddMinutes(1);
        reg.OnStop(300, ft, 0, now);
        now = T0.AddMinutes(1 + 95);
        reg.ObserveSnapshot([], now);
        Assert.Equal(1, reg.NamedCount);
        svc.Services.Add("Changed");
        reg.Get(svc.Key)!.Services.Add("Changed too");
        Assert.Equal(new[] { "Winmgmt" }, reg.Get(svc.Key)!.Services);
        Assert.Equal("svchost.exe (Winmgmt)", reg.Get(svc.Key)!.DisplayName);
    }

    /// <summary>
    /// An incident open longer than the raw history counts PoolWatch's own clears that freed part of its tag after the onset,
    /// also once the history no longer holds them: its growth (what the tag would have reached had they freed nothing, not
    /// the growth ending) and what they freed are what a history holding the whole incident gives, a clear inside the
    /// history added to one that left it.
    /// </summary>
    [Fact]
    public void AnIncidentOpenLongerThanTheRawHistoryCountsTheClearsThatLeftIt()
    {
        Incident Run(History history, out History h)
        {
            string dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var w = new World(history) { Interval = TimeSpan.FromSeconds(10) };
            var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry);
            var det = new Detector(settings);
            var clears = new List<Core.Cache.ClearRecord>();
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "crawler.exe");
            long tag = 100L << 20;
            for (int i = 0; i < 650; i++) // 108 minutes, growing 1 MB a sample from minute 5; clears at minutes 20 and 95
            {
                w.Io(100, other: 1000);
                if (i >= 30) tag += 1L << 20;
                if (i == 121) tag -= 40L << 20; // the first clear frees 40 MB of it
                if (i == 571) tag -= 30L << 20; // the second 30 MB
                w.Sample(("Reff", tag));
                var now = w.Now - w.Interval;
                mgr.Process(w.History, det, det.Evaluate(w.History, new OwnClears(clears, TimeSpan.FromMinutes(2))), At(now));
                if (i is 120 or 570)
                    clears.Add(new Core.Cache.ClearRecord { TimeUtc = now.AddSeconds(1), TickMs = At(now.AddSeconds(1)).Ms, Mode = Core.Cache.ClearMode.Standby, Trigger = Core.Cache.ClearTrigger.Manual, Before = default });
            }
            h = w.History;
            return Assert.Single(mgr.Open);
        }
        var inc = Run(new History(), out var raw);
        var whole = Run(new History(rawSpan: TimeSpan.FromMinutes(180), maxRawSamples: 2000), out var all);
        Assert.True(raw.Ms[0] > At(World.T0.AddSeconds(10 * 133)).Ms, "the first clear and its measurement left the history");
        Assert.True(all.Ms[0] <= whole.OnsetMs, "the long history holds the onset");
        Assert.True(whole.ClearedBytes > 40L << 20 && whole.Growth > 560L << 20, $"cleared {whole.ClearedBytes}, growth {whole.Growth}");
        // The level the tag would have reached without the clears, and what they freed, are the same; the onset each found is
        // within a few samples (the long history refines it on more samples), so the growth differs by the start levels.
        Assert.Equal((whole.PeakBytes, whole.ClearedBytes, whole.ClearedAtUtc), (inc.PeakBytes, inc.ClearedBytes, inc.ClearedAtUtc));
        Assert.InRange(inc.OnsetMs - whole.OnsetMs, -60_000, 60_000);
        Assert.Equal(whole.Growth + whole.StartBytes - inc.StartBytes, inc.Growth);
        Assert.Null(inc.ClearedAtUtc); // two clears freed part of it
    }
}

/// <summary>What handle scans hold, measured on the whole heap: on its own (<see cref="FootprintCollection"/>).</summary>
[Collection(FootprintCollection.Name)]
public sealed class HandleScanFootprint(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The heap <paramref name="scans"/> handle scans hold, each made from a fresh set of dictionaries let go at once.</summary>
    internal static long Of(int scans, Func<Dictionary<ProcessKey, Dictionary<string, int>>> make)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        var held = new List<TypedHandles>();
        for (int i = 0; i < scans; i++) held.Add(new TypedHandles(DateTime.UnixEpoch, make()) { Ms = i });
        long bytes = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(held);
        return bytes;
    }

    /// <summary>
    /// 18 scans of 700 processes with 15 handle types each (70 minutes of a busy PC's scans) hold under 3 MB: as the
    /// per-process dictionaries they come in, about 7.5 MB (a 15-entry dictionary is about 560 bytes, 12,600 of them).
    /// </summary>
    [Fact]
    public void SeventyMinutesOfABusyPcsHandleScansHoldUnderThreeMegabytes()
    {
        string[] types = ["File", "Event", "Key", "Section", "Thread", "Mutant", "Semaphore", "ALPC Port", "WaitCompletionPacket", "IoCompletion", "TpWorkerFactory", "Directory", "Token", "Desktop", "Timer"];
        var rnd = new Random(4);
        long bytes = Of(18, () => Enumerable.Range(0, 700).ToDictionary(p => new ProcessKey(4 * p + 8, 1000 + p), _ => types.ToDictionary(t => t, _ => rnd.Next(1, 3000))));
        output.WriteLine($"{bytes / 1048576.0:0.00} MB for 18 scans");
        Assert.True(bytes < 3L << 20, $"{bytes / 1048576.0:0.00} MB for 18 scans");
    }
}
