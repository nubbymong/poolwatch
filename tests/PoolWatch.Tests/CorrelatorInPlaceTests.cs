using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.History;

namespace PoolWatch.Tests;

/// <summary>
/// The correlator reads each process's points from the history in place (<see cref="History.Points"/>) instead of copying
/// a series per process, and computes activity only for the processes it keeps. The ranking itself must not change, nor
/// the names of processes the registry forgot, nor the incidents' last-activity check, which reads in place too.
/// </summary>
public class CorrelatorInPlaceTests
{
    /// <summary>
    /// Every number of the ranking of the churn hour, as the correlator computed it before it read the history in place.
    /// Culture-, time-zone- and newline-free (exact doubles, no formatted text, every line ended by "\n" rather than the
    /// platform's newline), so it holds on any machine and OS. Change it only with a deliberate change to the ranking maths.
    /// </summary>
    private const string Golden = "28A5959D88056B61D224A64ECF1EFC47D581082A5204FBFE4D5E01C2FE8346DB";

    [Fact]
    public void ThePointsReadInPlaceAreTheSeriesTheHistoryCopies()
    {
        var w = new ChurnWorld();
        for (int i = 0; i < 500; i++) w.Step(shortLived: 4, busy: 2, tagGrowth: 0); // past the raw cap: the history has been trimmed
        var h = w.History;
        Assert.True(h.Count < 500);
        foreach (var key in h.ProcessesSince(0))
        {
            var series = h.ProcessSeries(key);
            var pts = h.Points(key);
            for (int i = 0; i < h.Count; i++)
                Assert.Equal(series[i], pts.Has(i) ? pts[i] : null);
        }
        Assert.False(h.Points(new Core.Model.ProcessKey(1, 1)).Has(0));
    }

    /// <summary>A suspect the registry no longer knows is named by its history: when it started, and its last sample.</summary>
    [Fact]
    public void AProcessTheRegistryForgotIsNamedByItsLastSample()
    {
        var (w, window) = ChurnWorld.GrowingHour();
        var h = w.History;
        int gone = 0, running = 0;
        foreach (var s in Correlator.Rank(h, new Core.ProcessRegistry(), ChurnWorld.Tag, window, maxSuspects: 2000))
        {
            int last = Array.FindLastIndex(h.ProcessSeries(s.Key), p => p is not null);
            DateTime? lastSeen = last >= h.Count - 1 ? null : h.Times[last]; // still in the latest sample: no exit to give
            if (lastSeen is null) running++; else gone++;
            Assert.Equal(Core.Incidents.TracedProcess.UnknownName(s.Key.CreateTime > 0 ? s.Key.CreateTimeUtc : null, lastSeen), s.DisplayName);
        }
        Assert.True(gone > 100 && running > 50, $"{gone} gone, {running} still running");
    }

    /// <summary>The retention check's last I/O, read in place: the last sample whose counters rose, or null.</summary>
    [Fact]
    public void TheLastActivityIsTheLastSampleWithIO()
    {
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        w.AddProcess(100, 4, "stops.exe");
        w.AddProcess(101, 4, "idle.exe");
        w.AddProcess(102, 4, "busy.exe");
        for (int i = 0; i < 30; i++)
        {
            if (i == 12) { w.AddProcess(103, 4, "late.exe", startIndex: i); w.AddProcess(104, 4, "late2.exe", startIndex: i); }
            if (i is >= 1 and <= 9) w.Io(100, other: 10);
            if (i == 12) w.Io(103, read: 5); // in its first sample only: there is no earlier sample to rise from
            if (i is 14 or 15) w.Io(104, read: 5);
            w.Io(102, other: 1);
            w.Sample(("Reff", 1 << 20));
        }
        var h = w.History;
        DateTime? Last(int pid) => Core.Incidents.IncidentManager.LastActivity(h, w.Procs[pid].Key);
        Assert.Equal(h.Times[9], Last(100));
        Assert.Null(Last(101));
        Assert.Equal(h.Times[^1], Last(102));
        Assert.Null(Last(103));
        Assert.Equal(h.Times[15], Last(104));
    }

    /// <summary>The correlator ranks the tag once and each counter into reused buffers; the result is Stats' to the bit.</summary>
    [Fact]
    public void TheReusedRanksGiveStatsSpearmanAtEveryLag()
    {
        var rnd = new Random(3);
        for (int n = 2; n <= 40; n++)
        {
            // Few distinct values, so most ranks are ties, as with counters that often do not move.
            var tag = Enumerable.Range(0, n).Select(_ => rnd.Next(5) * 1048576.0).ToArray();
            var ranks = new Correlator.TagRanks(tag);
            for (int trial = 0; trial < 5; trial++)
            {
                var d = Enumerable.Range(0, n).Select(_ => (double)rnd.Next(4)).ToArray();
                for (int lag = 0; lag <= Correlator.MaxLag; lag++)
                {
                    double expected = lag == 0 ? Stats.Spearman(tag, d) : Stats.SpearmanLagged(tag, d, lag);
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(ranks.Spearman(d, lag)));
                }
            }
        }
    }

    [Fact]
    public void RankingAChurnHourAllocatesForTheSuspectsNotForEveryProcess()
    {
        var (w, window) = ChurnWorld.GrowingHour();
        Assert.True(w.History.ProcessesSince(0).Count > 9000);
        Correlator.Rank(w.History, w.Registry, ChurnWorld.Tag, window, maxSuspects: 2000); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        Correlator.Rank(w.History, w.Registry, ChurnWorld.Tag, window, maxSuspects: 2000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32L << 20, $"one ranking allocated {allocated >> 20} MB"); // 2.9 GB when every process was copied and correlated
    }

    [Fact]
    public void TheRankingOfAChurnHourIsUnchanged()
    {
        var (w, window) = ChurnWorld.GrowingHour();
        var suspects = Correlator.Rank(w.History, w.Registry, ChurnWorld.Tag, window, maxSuspects: 2000);
        var owners = OwnerRanking.Rank(w.History, suspects, ChurnWorld.Tag, window, 360L << 20, s => OwnerKeepTests.Identity(w.Registry, s));
        var sb = new StringBuilder();
        foreach (var s in suspects)
            Line(sb, s.Rank, s.Key, s.Image, s.DisplayName, Bits(s.Score), s.Confidence, s.Direct.HandleType, s.Direct.HandlesBefore, s.Direct.HandlesAfter,
                Bits(s.Direct.HandleShare), s.Direct.QuotaDelta, Bits(s.Direct.QuotaShare), s.Direct.Present, s.Activity.Counter, Bits(s.Activity.Spearman),
                s.Activity.BestLag, Bits(s.Activity.LaggedSpearman), Bits(s.Activity.IoShare), s.Activity.OpsDelta, s.Activity.Present,
                s.Lifecycle.StartedBeforeOnset, s.Lifecycle.ExitedAtEnd, s.Lifecycle.OrphanActive, s.Lifecycle.Notes.Count, s.Evidence.Count, s.ExitUtc?.Ticks);
        foreach (var o in owners)
            Line(sb, o.Rank, o.Owner.Key, Bits(o.Score), o.Confidence, o.ExplainedBytes, Bits(o.IoShare), o.Ops, Bits(o.Correlation), o.Meaningful, o.Plain.Count,
                string.Join(",", o.Members.Select(m => m.Key)));
        Assert.True(suspects.Count >= 1000, $"{suspects.Count} suspects");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        Assert.True(digest == Golden, digest);

        static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
        // "\n", not AppendLine: that writes Environment.NewLine ("\r\n" on Windows, "\n" on Linux), and one golden must hold on both.
        static void Line(StringBuilder sb, params object?[] values) =>
            sb.Append(string.Join(" ", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Append('\n');
    }
}
