using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Model;
using PoolWatch.Core.Growth;

namespace PoolWatch.Tests;

/// <summary>
/// The growth and clear stats: what PoolWatch's own clears freed of the kernel pool (and of each tag), and the pool's
/// change with those clears added back (the detector's baseline), added up over the window before it is said as growth or
/// a fall on its own; the real change while measured, and the rate per hour measured; kept by the minute for 25 hours,
/// bounded, and said in the approved words.
/// </summary>
public class GrowthLedgerTests
{
    private const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(2), Step = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Gap = Step + TimeSpan.FromMinutes(2);
    private static readonly PoolTag Reff = PoolTag.Parse("Reff"), Toke = PoolTag.Parse("Toke"), Gone = PoolTag.Parse("Gone");

    private static PoolTagEntry Tag(PoolTag tag, long bytes) => new(tag, 0, 0, (ulong)bytes, 0, 0, 0);

    private static ClearRecord ClearAt(DateTime utc, string? error = null) => new()
    {
        TimeUtc = utc, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = default, Error = error,
        After = error is null ? default(PoolTotals) : null,
    };

    /// <summary>
    /// Three hours a sample every 10 seconds: Reff (a cache) grows 2 MB a sample and each of two clears frees 600 MB of it
    /// as it goes on growing in the clear's 2 minutes; Toke leaks 20 KB a sample and the clears free none of it (it rises
    /// across them); Gone falls 200 MB on its own; a clear that failed is followed by a 100 MB fall of Reff on its own;
    /// two clears 40 seconds apart are one interval. The total is the three on 4 GB.
    /// </summary>
    private sealed class Run
    {
        public readonly GrowthLedger Ledger = new(Settle, Step);
        public readonly History History = History.ForInterval(10, rawMinutes: 24 * 60);
        public readonly List<ClearRecord> Clears = [];
        public readonly List<long> Totals = [];
        public readonly Dictionary<PoolTag, List<long>> Tags = new() { [Reff] = [], [Toke] = [], [Gone] = [] };
        public int Count;
        public DateTime Last;

        /// <summary>A sample on the 10-second grid, or at <paramref name="at"/>.</summary>
        public void Sample(long reff, long toke, long gone, DateTime? at = null)
        {
            var t = at ?? T0 + Step * Count;
            Count++;
            Last = t;
            long total = 4 * GB + reff + toke + gone;
            var tags = new List<PoolTagEntry> { Tag(Reff, reff), Tag(Toke, toke), Tag(Gone, gone) };
            Totals.Add(total);
            Tags[Reff].Add(reff); Tags[Toke].Add(toke); Tags[Gone].Add(gone);
            History.Add(new Sample(t, new PoolTotals((ulong)total, 0, 0, 0), tags, []));
            Ledger.Observe(t, total, tags, Clears);
        }

        public void Clear(string? error = null) => Clears.Add(ClearAt(Last + TimeSpan.FromSeconds(1), error));
    }

    private static Run ThreeHours()
    {
        var r = new Run();
        long reff = 200 * MB, toke = 600 * MB, gone = 300 * MB;
        int[] clearsAfter = [359, 719];
        for (int i = 0; i < 1080; i++)
        {
            reff += 2 * MB;
            toke += 20 * KB;
            if (i == 300) gone -= 200 * MB;
            if (clearsAfter.Any(k => i == k + 1)) reff -= 600 * MB;
            if (i == 901) reff -= 100 * MB; // on its own, after the failed clear
            if (i == 1001) reff -= 50 * MB;  // the merged pair frees 50 MB
            r.Sample(reff, toke, gone);
            if (clearsAfter.Contains(i)) r.Clear();
            if (i == 900) r.Clear("access denied");
            if (i == 1000) r.Clear();
            if (i == 1004) r.Clear(); // 40 s later, inside the first one's 2 minutes: one interval
        }
        return r;
    }

    /// <summary>
    /// What the detector's baseline says of one series (its values at every sample), counted independently of the ledger:
    /// a drop across a clear's interval is the clear's (OwnClears, ClearSpans); every other change between two samples
    /// outside an interval is the series' own, added up, unless a gap (a sleep) lies between them; the sum is growth or a
    /// fall.
    /// </summary>
    private static GrowthCounts Expected(History h, List<ClearRecord> clears, IReadOnlyList<long> x)
    {
        var spans = new OwnClears(clears, Settle).SpansIn(h);
        long change = 0, cleared = 0;
        int last = 0;
        bool gap = false;
        for (int i = 1; i < x.Count; i++)
        {
            if (h.Times[i] - h.Times[i - 1] > Gap) gap = true;
            if (spans.Inside(i)) continue;
            long d = x[i] - x[last];
            bool across = spans.Spans.Any(s => s.Before == last && s.Measured == i);
            if (across && d < 0) cleared -= d;
            else if (!gap) change += d;
            gap = false;
            last = i;
        }
        return GrowthCounts.Of(change, cleared);
    }

    [Fact]
    public void ClearedAndTheChangeWithTheClearsAddedBackAreTheDetectorsBaselineAndAddUpToTheRealChange()
    {
        var r = ThreeHours();
        var w = r.Ledger.Window(TimeSpan.FromHours(24));
        Assert.Equal(1080, r.History.Count);
        // The total: exactly the baseline's count, and its net is the real level change.
        var expected = Expected(r.History, r.Clears, r.Totals);
        Assert.Equal(expected, w.Total);
        Assert.Equal(r.Totals[^1] - r.Totals[0], w.Total.Net);
        Assert.Equal(w.Total.Grew - w.Total.Cleared - w.Total.Fell, w.Total.Net);
        // On the stitch, the change with every clear's drop added back is what grew (less what fell).
        var stitched = r.Totals.ToArray();
        var spans = new OwnClears(r.Clears, Settle).SpansIn(r.History);
        spans.Stitch(stitched, 0);
        Assert.Equal(stitched[^1] - stitched[0], w.Total.Change);
        Assert.Equal(spans.Spans.Sum(s => ClearSpan.Drop(r.Totals[s.Before], r.Totals[s.Measured])), w.Total.Cleared);
        Assert.True(w.Total.Grew > 0);
        Assert.Equal(0, w.Total.Fell); // growth and a fall are never both said: what fell is netted with what grew
        // The parts: Reff's two drops and the pair's 50 MB are the clears', its 100 MB after the failed clear is netted with
        // its growth; Gone fell 200 MB on its own; Toke grew across every clear (nothing of it cleared).
        Assert.Equal(4, w.Clears); // the failed one is not a clear
        Assert.Null(w.OneClearUtc);
        Assert.Equal(3, spans.Count);
        foreach (var tag in new[] { Reff, Toke, Gone })
        {
            var want = Expected(r.History, r.Clears, r.Tags[tag]);
            var got = w.Tags[tag];
            Assert.InRange(got.Grew, want.Grew - KB, want.Grew + KB);       // to the kilobyte a minute keeps
            Assert.InRange(got.Cleared, want.Cleared - KB, want.Cleared);
            Assert.InRange(got.Fell, want.Fell - KB, want.Fell + KB);
            Assert.InRange(got.Net - (r.Tags[tag][^1] - r.Tags[tag][0]), -2 * KB, 2 * KB);
        }
        Assert.Equal((0L, 200 * MB), (w.Tags[Gone].Grew, w.Tags[Gone].Fell));
        Assert.Equal(0, w.Tags[Toke].Cleared);
        Assert.Equal(0, w.Tags[Reff].Fell);
        // Reff's growth inside each clear's 2 minutes is the clear's: each of the two is credited 600 MB less the 26 MB it
        // grew in their 13 samples, the merged pair 50 MB less its 17 samples' 34 MB.
        Assert.InRange(w.Tags[Reff].Cleared, (2 * 574 + 16) * MB - KB, (2 * 574 + 16) * MB);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(60)]
    public void AWobbleIsNeitherGrowthNorAFallAndALeakReadsTheSameAtAnySamplingInterval(int seconds)
    {
        // Six hours of a pool that only wobbles (each sample up to 1 MB off its level, at random), and the same with a
        // 100 MB/h leak under it. Counting every step up as growth read about 200 MB/h for the first at 10 s, and ten times
        // that at 1 s; added up over the window, only where the wobble stood at the two ends is left.
        var step = TimeSpan.FromSeconds(seconds);
        var rnd = new Random(7);
        var flat = new GrowthLedger(Settle, step);
        var leak = new GrowthLedger(Settle, step);
        int n = (int)(TimeSpan.FromHours(6) / step) + 1;
        long first = 0, last = 0;
        for (int i = 0; i < n; i++)
        {
            long noise = (long)((rnd.NextDouble() * 2 - 1) * MB);
            var t = T0 + step * i;
            flat.Observe(t, 4 * GB + noise, [Tag(Reff, GB + noise)], []);
            leak.Observe(t, 4 * GB + noise + (long)(100 * MB * (step * i).TotalHours), [], []);
            if (i == 0) first = noise;
            last = noise;
        }
        var w = flat.Window(TimeSpan.FromHours(6));
        Assert.Equal(TimeSpan.FromHours(6), w.Measured);
        Assert.Equal(last - first, w.Total.Change);
        Assert.InRange(w.Total.Grew + w.Total.Fell, 0, 2 * MB);
        Assert.InRange(Math.Abs(w.PerHour(w.Total)!.Value), 0, MB / 3);
        Assert.InRange(w.Tags[Reff].Grew + w.Tags[Reff].Fell, 0, 2 * MB);
        var l = leak.Window(TimeSpan.FromHours(6));
        Assert.InRange(l.PerHour(l.Total)!.Value, 99 * MB, 101 * MB);
        Assert.Equal(0, l.Total.Fell);
    }

    [Fact]
    public void AClearIsCountedOnceItIsMeasuredAndItsDropIsNeverAFall()
    {
        var r = new Run();
        long reff = 800 * MB;
        for (int i = 0; i < 60; i++) r.Sample(reff, 0, 0);
        r.Clear();
        r.Sample(reff - 500 * MB, 0, 0); // inside the clear's 2 minutes
        var during = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((0, default(GrowthCounts)), (during.Clears, during.Total));
        for (int i = 0; i < 12; i++) r.Sample(reff - 480 * MB, 0, 0);
        var after = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal(1, after.Clears);
        Assert.Equal(r.Clears[0].TimeUtc, after.OneClearUtc);
        Assert.Equal(new GrowthCounts(0, 480 * MB, 0), after.Total); // the 20 MB refill in its 2 minutes is the clear's
        Assert.Equal(-480 * MB, after.Total.Net);
    }

    [Fact]
    public void ASampleExactlyAtTheSettleTimeEndsTheClearsInterval()
    {
        // A clear measured exactly on a sample's time: that sample is its measurement (as the detector's spans take it), so
        // the fall at the next sample is the pool's own, not the clear's.
        var r = new Run();
        for (int i = 0; i < 30; i++) r.Sample(800 * MB, 0, 0);
        r.Clear();                                                    // at the last sample + 1 s
        for (int i = 0; i < 11; i++) r.Sample(300 * MB, 0, 0);       // inside its 2 minutes
        r.Sample(300 * MB, 0, 0, at: r.Clears[0].TimeUtc + Settle);  // exactly 2 minutes after it
        r.Sample(200 * MB, 0, 0, at: r.Clears[0].TimeUtc + Settle + Step);
        var w = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal(Expected(r.History, r.Clears, r.Totals), w.Total);
        Assert.Equal((1, new GrowthCounts(0, 500 * MB, 100 * MB)), (w.Clears, w.Total));
    }

    [Fact]
    public void AFailedClearsDropFallsOnItsOwn()
    {
        var r = new Run();
        for (int i = 0; i < 30; i++) r.Sample(800 * MB, 0, 0);
        r.Clear("access denied");
        for (int i = 0; i < 30; i++) r.Sample(600 * MB, 0, 0);
        var w = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((0, new GrowthCounts(0, 0, 200 * MB)), (w.Clears, w.Total));
    }

    [Fact]
    public void TheRateIsPerHourMeasuredAndAChangeAcrossASleepIsNotCounted()
    {
        // Two hours, a 6-hour sleep, two hours: 4 of the 24 hours measured. The pool grew 100 MB an hour while awake and
        // 50 MB across the sleep. The change across the sleep was not measured: it is not counted, so the rate is the
        // 100 MB an hour measured and the rate times the hours measured is the net change.
        var r = new Run();
        long toke = 600 * MB;
        const long inc = 100 * MB / 360;
        for (int i = 0; i < 720; i++) { r.Sample(0, toke, 0); toke += inc; }
        r.Count += 6 * 360; // asleep
        toke += 50 * MB;
        for (int i = 0; i < 720; i++) { r.Sample(0, toke, 0); toke += inc; }
        var day = r.Ledger.Window(TimeSpan.FromHours(24));
        Assert.InRange(day.Measured, TimeSpan.FromHours(4) - TimeSpan.FromSeconds(30), TimeSpan.FromHours(4));
        Assert.False(day.Whole);
        Assert.True(day.StartedInside);
        Assert.Equal(2 * 719 * inc, day.Total.Grew);
        Assert.Equal(r.Totals[^1] - r.Totals[0] - 50 * MB - inc, day.Total.Net);
        Assert.Equal((long)(day.Total.Change / day.Measured.TotalHours), day.PerHour(day.Total));
        Assert.InRange(day.PerHour(day.Total)!.Value, 99 * MB, 101 * MB);
        Assert.Equal(Expected(r.History, r.Clears, r.Totals), day.Total);
        // 3 h 59 min 40 s measured (a sample interval is lost at the sleep): said to the nearest minute.
        Assert.Equal("Measured over 4 h of the 24: PoolWatch started " + StartedPhrase(day) + ".", GrowthText.Measured(day));
        var hour = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.True(hour.Whole);
        Assert.Equal("Measured over 1 h.", GrowthText.Measured(hour));
        // The same sleep with PoolWatch already running at the window's start: said as time not sampled, a sleep for one.
        var six = r.Ledger.Window(TimeSpan.FromHours(6));
        Assert.False(six.StartedInside);
        Assert.Equal("Measured over 2 h of the 6: the rest was not sampled, for example while the PC was asleep.", GrowthText.Measured(six));
    }

    [Fact]
    public void AClearLeftOpenAcrossASleepKeepsItsDropButARiseAcrossItIsNotCounted()
    {
        var r = new Run();
        for (int i = 0; i < 60; i++) r.Sample(800 * MB, 0, 0);
        r.Clear();
        r.Count += 360; // asleep for an hour before the clear is measured
        for (int i = 0; i < 30; i++) r.Sample(300 * MB, 0, 0);
        Assert.Equal((1, new GrowthCounts(0, 500 * MB, 0)), (r.Ledger.Window(TimeSpan.FromHours(24)).Clears, r.Ledger.Window(TimeSpan.FromHours(24)).Total));
        var up = new Run();
        for (int i = 0; i < 60; i++) up.Sample(800 * MB, 0, 0);
        up.Clear();
        up.Count += 360;
        for (int i = 0; i < 30; i++) up.Sample(900 * MB, 0, 0);
        var u = up.Ledger.Window(TimeSpan.FromHours(24));
        Assert.Equal((1, default(GrowthCounts)), (u.Clears, u.Total));
    }

    /// <summary>"at 12:20", or "yesterday at 23:20" where local midnight falls between the start and now (any time zone).</summary>
    private static string StartedPhrase(GrowthWindow w) => StartedPhrase(w.StartedUtc!.Value, w.NowUtc);

    private static string StartedPhrase(DateTime startedUtc, DateTime nowUtc)
    {
        var started = startedUtc.ToLocalTime();
        string at = "at " + started.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return started.Date == nowUtc.ToLocalTime().Date ? at : "yesterday " + at;
    }

    [Fact]
    public void AClockSetBackNeitherInflatesTheRateNorCountsAClearTwice()
    {
        // Two hours at 600 MB/h with a clear at 1 h 50 min that frees 300 MB; then the clock is set back an hour (the pool
        // moved 500 MB across the step), and 70 more minutes at 600 MB/h, passing the clear's time again.
        var ledger = new GrowthLedger(Settle, Step);
        var clears = new List<ClearRecord>();
        long level = 4 * GB;
        const long perSample = 600 * MB / 360;
        var t = T0;
        for (int i = 0; i < 720; i++)
        {
            ledger.Observe(t, level, [Tag(Reff, level - 3 * GB)], clears);
            if (i == 659) { clears.Add(ClearAt(t + TimeSpan.FromSeconds(1))); level -= 300 * MB; }
            t += Step;
            level += perSample;
        }
        Assert.Equal(1, ledger.Window(TimeSpan.FromHours(24)).Clears);
        t -= TimeSpan.FromHours(1);
        level += 500 * MB;
        for (int i = 0; i < 70 * 6; i++)
        {
            ledger.Observe(t, level, [Tag(Reff, level - 3 * GB)], clears);
            if (i == 1)
            {
                // The minute before the new time's (6 changes), the new time's own minute as it was counted before the step
                // (6: kept, so at most the step's length is counted twice), and the change measured since (1).
                var first = ledger.Window(TimeSpan.FromMinutes(1));
                Assert.Equal(13 * perSample, first.Total.Change);
                Assert.InRange(first.Tags[Reff].Change, 13 * perSample - KB, 13 * perSample + KB);
            }
            t += Step;
            level += perSample;
        }
        var hour = ledger.Window(TimeSpan.FromHours(1));
        Assert.InRange(hour.PerHour(hour.Total)!.Value, 590 * MB, 620 * MB); // not twice that
        // The day: the hour before the new time (359 changes), the new time's minute as counted before (6) and the 419
        // since the step; the minutes that lay wholly ahead of the new time went, with the clear in them, and passing its
        // time again does not count it a second time.
        var day = ledger.Window(TimeSpan.FromHours(24));
        Assert.Equal(GrowthCounts.Of(784 * perSample, 0), day.Total);
        Assert.InRange(day.Tags[Reff].Change, 784 * perSample - KB, 784 * perSample + KB);
        Assert.Equal(0, day.Clears);
        Assert.Equal(T0, day.StartedUtc);
        Assert.Equal(0, ledger.PassedClears); // let go once the clock passed it again

        // Set back to before PoolWatch started: everything after the new time goes, and it counts as started there.
        var early = new GrowthLedger(Settle, Step);
        for (int i = 0; i < 60; i++) early.Observe(T0 + Step * i, 4 * GB, [], []);
        var back = T0 - TimeSpan.FromMinutes(50);
        for (int i = 0; i < 6 * 40; i++) early.Observe(back + Step * i, 4 * GB + i * MB, [], []);
        var w = early.Window(TimeSpan.FromHours(1));
        Assert.Equal(back, w.StartedUtc);
        Assert.Equal(239 * MB, w.Total.Grew);
    }

    [Fact]
    public void AClearRightAfterTheClockWasSetBackIsCounted()
    {
        // The clock goes back half an hour; a clear runs just after the first sample on the new time and frees 1 GB.
        var ledger = new GrowthLedger(Settle, Step);
        var clears = new List<ClearRecord>();
        var t = T0;
        for (int i = 0; i < 60; i++) { ledger.Observe(t, 4 * GB, [], clears); t += Step; }
        t -= TimeSpan.FromMinutes(30);
        ledger.Observe(t, 4 * GB, [], clears);
        clears.Add(ClearAt(t + TimeSpan.FromSeconds(1)));
        for (int i = 1; i < 30; i++) ledger.Observe(t + Step * i, 3 * GB, [], clears);
        var w = ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((1, new GrowthCounts(0, GB, 0)), (w.Clears, w.Total));
    }

    [Theory]
    [InlineData(7 * 60 + 10, 7 * 60 + 5)]  // back 15 s from the step's next sample, inside the minute being filled
    [InlineData(8 * 60, 7 * 60 + 55)]      // back into the minute before, where the clear was counted
    public void AClockSetBackByAFewSecondsKeepsTheMinuteItFallsIn(int lastSecond, int newSecond)
    {
        // Five minutes at 4 GB; a clear at 06:04:51 frees 1 GB and is measured by the 06:07:00 sample; a few samples more;
        // then the clock is set back a few seconds (a time sync), as Windows may step it.
        var ledger = new GrowthLedger(Settle, Step);
        var clears = new List<ClearRecord>();
        for (int s = 0; s < 5 * 60; s += 10) ledger.Observe(T0.AddSeconds(s), 4 * GB, [Tag(Reff, GB)], clears);
        clears.Add(ClearAt(T0.AddSeconds(4 * 60 + 51)));
        for (int s = 5 * 60; s <= lastSecond; s += 10) ledger.Observe(T0.AddSeconds(s), 3 * GB, [Tag(Reff, 10 * MB)], clears);
        var before = ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((1, GB), (before.Clears, before.Total.Cleared));

        ledger.Observe(T0.AddSeconds(newSecond), 3 * GB, [Tag(Reff, 10 * MB)], clears);
        ledger.Observe(T0.AddSeconds(newSecond + 10), 3 * GB + 10 * MB, [Tag(Reff, 20 * MB)], clears);
        var after = ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((1, GB), (after.Clears, after.Total.Cleared)); // the clear is still counted, once
        Assert.Equal(GB - 10 * MB, after.Tags[Reff].Cleared);
        Assert.Equal(10 * MB, after.Total.Grew);                    // and what was measured after the step
        Assert.Equal(T0, after.StartedUtc);
    }

    [Fact]
    public void ATagIsLetGoAfterAClockThatWasAheadIsSetBack()
    {
        // The clock is a year ahead while a tag grows, then put right; the tag is small again from then on. Its slot is let
        // go once no kept minute holds it, not a year later.
        var ledger = new GrowthLedger(Settle, TimeSpan.FromMinutes(1));
        var ahead = T0.AddYears(1);
        for (int i = 0; i < 5; i++) ledger.Observe(ahead.AddMinutes(i), 4 * GB, [Tag(Toke, 5 * MB + i * MB)], []);
        for (int i = 0; i < 26 * 60; i++) ledger.Observe(T0.AddMinutes(i), 4 * GB, [Tag(Toke, MB)], []);
        Assert.Equal(0, ledger.TagCount);
    }

    [Fact]
    public void BeforeHalfAnHourIsMeasuredTheRateSaysWhenItWillBeKnown()
    {
        var r = new Run();
        Assert.Equal("known in 30 min", GrowthText.PerHour(r.Ledger.Window(TimeSpan.FromHours(24))));
        Assert.Equal(GrowthText.NotKnown, GrowthText.SimpleNet(r.Ledger.Window(TimeSpan.FromHours(24))));
        Assert.Equal("Nothing measured yet: the rate shows after 30 min.", GrowthText.Measured(r.Ledger.Window(TimeSpan.FromHours(24))));
        long toke = 600 * MB;
        for (int i = 0; i <= 12 * 6; i++) { r.Sample(0, toke, 0); toke += MB; } // 12 minutes
        var w = r.Ledger.Window(TimeSpan.FromHours(24));
        Assert.False(w.RateKnown);
        Assert.Null(w.PerHour(w.Total));
        Assert.Equal("known in 18 min", GrowthText.PerHour(w));
        Assert.Equal("", GrowthText.TagPerHour(w, w.Tags[Toke]));
        Assert.Equal("Measured over 12 min so far: the rate shows after 30 min.", GrowthText.Measured(w));
        Assert.Equal("up 72 MB", GrowthText.SimpleNet(w)); // what grew is a fact from the first minutes
        for (int i = 0; i < 17 * 6 + 5; i++) { r.Sample(0, toke, 0); toke += MB; } // 29 min 50 s: never "30 min so far"
        Assert.Equal("Measured over 29 min so far: the rate shows after 30 min.", GrowthText.Measured(r.Ledger.Window(TimeSpan.FromHours(24))));
        Assert.Equal("known in 1 min", GrowthText.PerHour(r.Ledger.Window(TimeSpan.FromHours(24))));
        r.Sample(0, toke, 0);
        Assert.True(r.Ledger.Window(TimeSpan.FromHours(24)).RateKnown); // 30 minutes
    }

    [Fact]
    public void AWindowCountsTheMinutesFromTheOneItStartsIn()
    {
        // 90 minutes: 300 MB in the first half hour, then 1 MB a minute. The last hour holds the changes of the samples from
        // the minute an hour ago on (at most a minute more than the hour), the day all of them.
        var r = new Run();
        long toke = 600 * MB;
        for (int i = 0; i < 540; i++) { r.Sample(0, toke, 0); toke += i < 180 ? 300 * MB / 180 : MB / 6; }
        var now = T0 + Step * 539;
        var from = now - TimeSpan.FromHours(1);
        var minute = new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        long want = Enumerable.Range(1, 539).Where(i => T0 + Step * i >= minute).Sum(i => r.Totals[i] - r.Totals[i - 1]);
        var hour = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal(want, hour.Total.Grew);
        Assert.InRange(hour.Total.Grew, 60 * MB, 72 * MB);
        Assert.Equal(TimeSpan.FromHours(1), hour.Measured); // never more than the window
        Assert.Equal(r.Totals[^1] - r.Totals[0], r.Ledger.Window(TimeSpan.FromHours(24)).Total.Grew);
    }

    [Fact]
    public void ATagIsCountedFromTheSampleItReachesFourMegabytesAndTheTablesStayBounded()
    {
        var ledger = new GrowthLedger(Settle, TimeSpan.FromMinutes(1));
        var small = PoolTag.Parse("Smal");
        // 26 hours a sample a minute; 300 tags of 5 MB that each wobble, more than it counts; Smal grows from 1 MB.
        var many = Enumerable.Range(0, 300).Select(k => PoolTag.Parse($"M{k:000}")).ToArray();
        for (int i = 0; i < 26 * 60; i++)
        {
            var tags = many.Select((t, k) => Tag(t, 5 * MB + (i + k) % 3 * KB)).ToList();
            tags.Add(Tag(small, MB + i * 64 * KB));
            ledger.Observe(T0.AddMinutes(i), 4 * GB, tags, []);
        }
        Assert.Equal(GrowthLedger.MaxTags, ledger.TagCount); // the first 256 (Smal came too late: the table was full)
        Assert.InRange(ledger.MinuteCount, 25 * 60, 25 * 60 + 2);
        // A wobble of a few kilobytes adds up to nothing: it is never kept, so the 25 hours hold no more than the fixed tables.
        Assert.Equal(0, ledger.TagEntries);
        Assert.InRange(ledger.ApproxBytes(), 1, 2L << 20);
        // Below the size, a tag is not counted: the first tags reached it at once and have counts, Smal has none.
        var w = ledger.Window(TimeSpan.FromHours(24));
        Assert.False(w.Tags.ContainsKey(small));
        Assert.Equal(GrowthLedger.MaxTags, w.Tags.Count);

        // The most it holds: 25 hours of 256 tags that each change by a megabyte every minute.
        var busy = new GrowthLedger(Settle, TimeSpan.FromMinutes(1));
        for (int i = 0; i < 26 * 60; i++)
            busy.Observe(T0.AddMinutes(i), 4 * GB, many.Select((t, k) => Tag(t, 5 * MB + (i + k) % 2 * MB)).ToList(), []);
        Assert.Equal(25L * 60 * 256, busy.TagEntries, 256L * 2);
        Assert.InRange(busy.ApproxBytes(), 4L << 20, 6L << 20);

        // A tag that is gone falls to nothing; small again, it is let go once no kept minute holds it. A large tag that does
        // not change stays counted, and one under the size is never counted.
        var fresh = new GrowthLedger(Settle, TimeSpan.FromMinutes(1));
        PoolTag once = PoolTag.Parse("Once"), flat = PoolTag.Parse("Flat"), tiny = PoolTag.Parse("Tiny");
        List<PoolTagEntry> At(int i) =>
        [
            Tag(flat, 10 * MB), Tag(tiny, MB + i % 2 * 512 * KB),
            .. i < 10 ? [Tag(once, 8 * MB + i * MB)] : i < 60 ? Array.Empty<PoolTagEntry>() : [Tag(small, 5 * MB + i * KB)],
        ];
        for (int i = 0; i < 2 * 60; i++) fresh.Observe(T0.AddMinutes(i), 4 * GB, At(i), []);
        var early = fresh.Window(TimeSpan.FromHours(24));
        Assert.Equal(new GrowthCounts(0, 0, 8 * MB), early.Tags[once]); // 8 MB to 17 MB, then gone: 8 MB lower
        Assert.Equal(default, early.Tags[flat]);
        Assert.False(early.Tags.ContainsKey(tiny));
        for (int i = 2 * 60; i < 27 * 60; i++) fresh.Observe(T0.AddMinutes(i), 4 * GB, At(i), []);
        Assert.Equal(2, fresh.TagCount); // Once was let go 25 hours after it last changed; Flat and Smal are counted
        var hour = fresh.Window(TimeSpan.FromHours(1));
        Assert.True(hour.Tags.ContainsKey(small));
        Assert.True(hour.Tags.ContainsKey(flat));
        Assert.False(hour.Tags.ContainsKey(once));
    }

    [Fact]
    public void ATagsSlotIsNotGivenToAnotherTagWhileAKeptMinuteHoldsIt()
    {
        // Aaaa grows a megabyte a minute for 26 hours, then falls under the size; Bbbb comes after. Aaaa's kept minutes of
        // the last day still hold its slot, so Bbbb gets a slot of its own and neither tag's day reads the other's minutes.
        var ledger = new GrowthLedger(Settle, TimeSpan.FromMinutes(1));
        PoolTag a = PoolTag.Parse("Aaaa"), b = PoolTag.Parse("Bbbb");
        int end = 26 * 60;
        for (int i = 0; i < end; i++) ledger.Observe(T0.AddMinutes(i), 4 * GB, [Tag(a, 5 * MB + i * MB)], []);
        ledger.Observe(T0.AddMinutes(end), 4 * GB, [Tag(a, MB)], []);
        ledger.Observe(T0.AddMinutes(end + 1), 4 * GB, [Tag(a, MB)], []);
        ledger.Observe(T0.AddMinutes(end + 2), 4 * GB, [Tag(a, MB), Tag(b, 8 * MB)], []);
        ledger.Observe(T0.AddMinutes(end + 3), 4 * GB, [Tag(a, MB), Tag(b, 8 * MB)], []);
        var day = ledger.Window(TimeSpan.FromHours(24));
        Assert.Equal(default, day.Tags[b]);
        // The day starts at minute end + 3 - 1440: Aaaa's megabyte a minute from there, then its fall to 1 MB.
        long aWas = 5 * MB + (end - 1) * MB, grewInDay = (end - (end + 3 - 24 * 60)) * MB;
        Assert.InRange(day.Tags[a].Change, grewInDay - (aWas - MB) - KB, grewInDay - (aWas - MB) + KB);
    }

    [Fact]
    public void AClearDatedAfterTheNewestSampleIsNotCountedYet()
    {
        // The clock was set back: a clear's time lies after the samples now taken. Until a sample reaches it, the changes are
        // the pool's own (as the detector leaves such a clear out).
        var r = new Run();
        for (int i = 0; i < 30; i++) r.Sample(800 * MB, 0, 0);
        r.Clears.Add(ClearAt(T0.AddHours(5)));
        for (int i = 0; i < 30; i++) r.Sample(600 * MB, 0, 0);
        var w = r.Ledger.Window(TimeSpan.FromHours(1));
        Assert.Equal((0, new GrowthCounts(0, 0, 200 * MB)), (w.Clears, w.Total));
    }

    // ------------------------------------------------------------------ the words and number formats of the approved mockup

    private static GrowthWindow Window(double hours, long grew, long cleared, long fell, int clears, double measuredHours, DateTime? one = null) => new()
    {
        Span = TimeSpan.FromHours(hours), NowUtc = T0.AddHours(30), Measured = TimeSpan.FromHours(measuredHours),
        Total = new GrowthCounts(grew, cleared, fell), Clears = clears, OneClearUtc = one, StartedUtc = T0,
    };

    [Fact]
    public void TheSimpleRowSaysTheLastDayInTheMockupsWords()
    {
        // The Simple view's mockup: 23,245 MB grew over 18 hours measured, 18 clears freed 21,811 MB.
        var w = Window(24, 23245 * MB, 21811 * MB, 0, 18, 18);
        Assert.Equal("Last 24 hours", GrowthText.WindowLabel(StatsWindow.Day, w));
        Assert.Equal("Without the clears it would grow", GrowthText.RateCaption(w));
        Assert.Equal("1.26 GB/h", GrowthText.PerHour(w));
        Assert.Equal("Cleared (18 clears)", GrowthText.ClearedLabel(w.Clears));
        Assert.Equal("21.3 GB", GrowthText.SimpleCleared(w));
        Assert.Equal("Net change", GrowthText.NetLabel);
        Assert.Equal("up 1.4 GB", GrowthText.SimpleNet(w));
        // The row adds up: the rate times the hours measured, less what the clears freed, is the net change.
        Assert.InRange(w.PerHour(w.Total)!.Value * 18 - w.Total.Cleared - w.Total.Net, -18, 18);
        Assert.Equal("down 300 MB", GrowthText.SimpleNet(Window(24, 0, 0, 300 * MB, 0, 24)));
        Assert.Equal("unchanged", GrowthText.SimpleNet(Window(24, 0, 0, 0, 0, 24)));
        Assert.Equal("0", GrowthText.SimpleCleared(Window(24, MB, 0, 0, 0, 24)));
        Assert.Equal("Cleared (1 clear)", GrowthText.ClearedLabel(1));
        Assert.Equal("The kernel pool's change with PoolWatch's own cache clears added back, for each hour PoolWatch measured.", GrowthText.PerHourTip);
    }

    [Fact]
    public void TheRatesCaptionSaysWhetherClearsRanAndWhetherThePoolGrewOrFell()
    {
        // With PoolWatch's own clears in the window: what the pool would do without them. With none: what it does.
        var fell = Window(24, 0, 2 * GB, 240 * MB, 5, 24);
        Assert.Equal(("Without the clears it would fall", "10 MB/h"), (GrowthText.RateCaption(fell), GrowthText.PerHour(fell)));
        Assert.Equal("down 2.23 GB", GrowthText.SimpleNet(fell));
        var grows = Window(24, 24 * 180 * MB, 0, 0, 0, 24);
        Assert.Equal(("It grows", "180 MB/h"), (GrowthText.RateCaption(grows), GrowthText.PerHour(grows)));
        var falls = Window(24, 0, 0, 300 * MB, 0, 24);
        Assert.Equal(("It falls", "12.5 MB/h"), (GrowthText.RateCaption(falls), GrowthText.PerHour(falls)));
        // No change says no direction, and neither does one under a byte an hour (the caption and the rate say it alike).
        var still = Window(24, 0, 0, 0, 0, 24);
        Assert.Equal(("Growth per hour", "nothing"), (GrowthText.RateCaption(still), GrowthText.PerHour(still)));
        var tiny = Window(6, 0, 0, 2, 0, 6);
        Assert.Equal(("Growth per hour", "nothing"), (GrowthText.RateCaption(tiny), GrowthText.PerHour(tiny)));
        // Before the rate is known nothing is said to grow or fall, with or without clears.
        Assert.Equal("known in 18 min", GrowthText.PerHour(Window(24, 0, 0, 0, 0, (12 * 60 + 5) / 3600.0))); // 17 min 55 s to go
        var soon = Window(24, 0, 0, 300 * MB, 1, 0.2);
        Assert.Equal(("Growth per hour", "known in 18 min"), (GrowthText.RateCaption(soon), GrowthText.PerHour(soon)));
        var early = Window(24, 600 * MB, 0, 0, 0, 0.2);
        Assert.Equal(("Growth per hour", "known in 18 min"), (GrowthText.RateCaption(early), GrowthText.PerHour(early)));
        var none = new GrowthLedger(Settle, Step).Window(TimeSpan.FromHours(24));
        Assert.Equal(("Growth per hour", "known in 30 min"), (GrowthText.RateCaption(none), GrowthText.PerHour(none)));
        Assert.Equal(GrowthText.NotKnown, GrowthText.SimpleCleared(none)); // as the net change says, not "0"
    }

    [Fact]
    public void TheSimpleRowSaysSinceWhenWhilePoolWatchHasRunForLessThanTheDay()
    {
        var since = Window(24, MB, 0, 0, 0, 3) with { StartedUtc = T0.AddHours(27) };
        string clock = T0.AddHours(27).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string expected = StartedPhrase(T0.AddHours(27), since.NowUtc) is var p && p.StartsWith("at ") ? "Since " + clock : "Since yesterday at " + clock;
        Assert.Equal(expected, GrowthText.WindowLabel(StatsWindow.Day, since));
        Assert.Equal("Last 24 hours", GrowthText.WindowLabel(StatsWindow.Day, Window(24, MB, 0, 0, 0, 18)));
    }

    [Fact]
    public void TheAdvancedPanelSaysEachWindowInTheMockupsWords()
    {
        Assert.Equal(["Last hour", "Last 6 hours", "Last 24 hours"], StatsWindow.All.Select(x => x.Name));
        Assert.Equal("Last 6 hours", StatsWindow.Default.Name);
        Assert.Equal("Growth and clears", GrowthText.Title);
        var six = Window(6, 7578 * MB, 7270 * MB, 0, 6, 6);
        Assert.Equal("1.23 GB/h", GrowthText.PerHour(six));
        Assert.Equal("+7.4 GB", GrowthText.Grew(six.Total.Grew));
        Assert.Equal("Cleared (6 clears)", GrowthText.ClearedLabel(six.Clears));
        Assert.Equal("−7.1 GB", GrowthText.Less(six.Total.Cleared));
        Assert.Equal(("Fell on its own", "0"), (GrowthText.FellLabel, GrowthText.Less(six.Total.Fell)));
        Assert.Equal(("Net change", "+308 MB"), (GrowthText.NetLabel, GrowthText.Signed(six.Total.Net)));
        Assert.Equal("Measured over 6 h.", GrowthText.Measured(six));
        Assert.Equal("Measured over 1 h.", GrowthText.Measured(Window(1, 1331 * MB, 1229 * MB, 0, 1, 1)));
        // A window measured all through but for its edges' minutes is said whole; a few minutes more missing is said.
        Assert.Equal("Measured over 6 h.", GrowthText.Measured(Window(6, GB, 0, 0, 0, 6 - 2.5 / 60)));
        Assert.Equal("Measured over 5 h 56 min of the 6: the rest was not sampled, for example while the PC was asleep.", GrowthText.Measured(Window(6, GB, 0, 0, 0, 6 - 4.0 / 60)));
        var day = Window(24, 23245 * MB, 21811 * MB, 0, 18, 18);
        Assert.Equal("Measured over 18 h of the 24: the rest was not sampled, for example while the PC was asleep.", GrowthText.Measured(day));
        // Each sleep loses a sample interval: 20 seconds short of 18 hours still reads as the mockup's "18 h".
        Assert.Equal("Measured over 18 h of the 24: the rest was not sampled, for example while the PC was asleep.", GrowthText.Measured(Window(24, GB, 0, 0, 0, 18 - 20.0 / 3600)));
        Assert.Equal("−615 MB", GrowthText.Less(615 * MB));
        Assert.Equal("−1.2 GB", GrowthText.Signed(-(long)(1.2 * GB)));
        Assert.Equal("0", GrowthText.Signed(0));
        var started = T0.AddHours(30).AddMinutes(-40);
        Assert.Equal("Measured over 40 min of the hour: PoolWatch started " + StartedPhrase(started, T0.AddHours(30)) + ".",
            GrowthText.Measured(Window(1, MB, 0, 0, 0, 40 / 60.0) with { StartedUtc = started }));
        Assert.Equal("Time window", GrowthText.PickerName);
    }

    [Fact]
    public void ATagsColumnsAndSentenceAreSaidInTheMockupsWords()
    {
        Assert.Equal("Grew, Cleared, Net change and Per hour of tags now: last 6 hours", GrowthText.WindowNote(StatsWindow.Default));
        Assert.Equal("Grew, Cleared, Net change and Per hour of tags now: last hour", GrowthText.WindowNote(StatsWindow.All[0]));
        var six = Window(6, 7578 * MB, 7270 * MB, 0, 6, 6);
        var reff = new GrowthCounts(5018 * MB, 4915 * MB, 0);
        Assert.Equal("+4.9 GB (now 912 MB)", GrowthText.TagGrew(reff, 912 * MB));
        Assert.Equal("4.8 GB", GrowthText.TagCleared(reff));
        Assert.Equal("+103 MB", GrowthText.TagNet(reff));
        Assert.Equal("836.3 MB/h", GrowthText.TagPerHour(six, reff));
        Assert.Equal("Last 6 hours: it grew 4.9 GB, PoolWatch's own cache clears freed 4.8 GB of it, and it is 103 MB higher. Without the clears it would grow 836.3 MB/h.",
            GrowthText.TagSentence(StatsWindow.Default, six, reff));
        var toke = new GrowthCounts(150 * MB, 0, 0);
        Assert.Equal(("+150 MB (now 760 MB)", "nothing", "+150 MB", "25 MB/h"),
            (GrowthText.TagGrew(toke, 760 * MB), GrowthText.TagCleared(toke), GrowthText.TagNet(toke), GrowthText.TagPerHour(six, toke)));
        // One clear in the last hour: said with its time, as the mockup's last hour.
        var clearAt = T0.AddHours(29).AddMinutes(30);
        var hour = Window(1, 1331 * MB, 1229 * MB, 0, 1, 1, clearAt);
        Assert.Equal($"Last hour: it grew 870 MB, PoolWatch's own cache clear at {Fmt.Clock(clearAt)[..5]} freed 840 MB of it, and it is 30 MB higher. Without the clears it would grow 870 MB/h.",
            GrowthText.TagSentence(StatsWindow.All[0], hour, new GrowthCounts(870 * MB, 840 * MB, 0)));
        // A window not measured all through says how long it was; a fall on its own is said; no clear, no clause, and the
        // rate is what it does, not what it would do without clears that never ran.
        var day = Window(24, 23245 * MB, 21811 * MB, 0, 18, 18);
        Assert.Equal("Last 24 hours (18 h measured): it grew 15.2 GB, PoolWatch's own cache clears freed 14.6 GB of it, and it is 615 MB higher. Without the clears it would grow 864.7 MB/h.",
            GrowthText.TagSentence(StatsWindow.Day, day, new GrowthCounts(15565 * MB, 14950 * MB, 0)));
        Assert.Equal("Last 6 hours: it did not grow, PoolWatch's own cache clears freed none of it, 400 MB fell on its own, and it is 400 MB lower.",
            GrowthText.TagSentence(StatsWindow.Default, six, new GrowthCounts(0, 0, 400 * MB)));
        Assert.Equal("Last 6 hours: it grew 60 MB and it is 60 MB higher. It grows 10 MB/h.",
            GrowthText.TagSentence(StatsWindow.Default, Window(6, 60 * MB, 0, 0, 0, 6), new GrowthCounts(60 * MB, 0, 0)));
        Assert.Equal("now 912 MB", GrowthText.TagGrew(new GrowthCounts(0, 0, 5 * MB), 912 * MB));
        Assert.Equal("−5 MB", GrowthText.TagNet(new GrowthCounts(0, 0, 5 * MB)));
        Assert.Equal("", GrowthText.TagPerHour(six, new GrowthCounts(0, 0, 5 * MB)));
    }

    [Fact]
    public void ATagsRowAndItsSentenceRoundEveryAmountAsTheStatsBesideTheGraphDo()
    {
        // 21.29 GB grew, 20.61 GB cleared, 102.6 MB higher: one decimal above a gigabyte and whole megabytes below it, as
        // the stats and the header say them, in the row and in its details alike.
        var c = new GrowthCounts(21800 * MB, 21105 * MB, 0);
        Assert.Equal(("+21.3 GB (now 21.3 GB)", "20.6 GB", "+695 MB"), (GrowthText.TagGrew(c, 21800 * MB), GrowthText.TagCleared(c), GrowthText.TagNet(c)));
        string said = GrowthText.TagSentence(StatsWindow.Default, Window(6, 21800 * MB, 21105 * MB, 0, 6, 6), c);
        Assert.Contains("it grew 21.3 GB, PoolWatch's own cache clears freed 20.6 GB of it, and it is 695 MB higher", said);
        Assert.DoesNotContain("21.29", said);
        var mb = new GrowthCounts((long)(102.6 * MB), 0, (long)(40.4 * MB));
        Assert.Equal(("+103 MB (now 1.2 GB)", "+62 MB"), (GrowthText.TagGrew(mb, 1229 * MB), GrowthText.TagNet(mb)));
        Assert.Contains("it grew 103 MB, 40 MB fell on its own, and it is 62 MB higher",
            GrowthText.TagSentence(StatsWindow.Default, Window(6, (long)(102.6 * MB), 0, (long)(40.4 * MB), 0, 6), mb));
    }

    // ------------------------------------------------------------------ RAM, a reading a minute

    [Fact]
    public void RamColumnsAverageTheMinutesInEachAndFollowTheLocalClock()
    {
        var ram = new RamHistory();
        long total = 128 * GB;
        // 90 minutes of readings: the cache grows 1 GB a minute from 40 GB, free is 20 GB, in use the rest.
        var start = new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 90; i++) ram.Add(start.AddMinutes(i).AddSeconds(7), total, 20 * GB, 40 * GB + i * GB);
        ram.Add(start.AddMinutes(89).AddSeconds(40), total, 20 * GB, 40 * GB + 89 * GB / 2); // a second reading in the minute replaces the first
        Assert.Equal(90, ram.Count);
        Assert.Equal(new RamPoint(start.AddMinutes(89), total - 20 * GB - (40 * GB + 89 * GB / 2), 40 * GB + 89 * GB / 2, 20 * GB), ram.At(89));
        var now = start.AddMinutes(89).AddSeconds(50);
        var minutes = ram.Columns(now, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        Assert.InRange(minutes.Count, 60, 61);
        Assert.All(minutes, c => Assert.Equal(total, c.InUse + c.Cache + c.Free));
        var quarters = ram.Columns(now, TimeSpan.FromHours(24), TimeSpan.FromMinutes(15));
        Assert.Equal(6, quarters.Count); // 90 minutes of quarter hours on the local clock
        Assert.All(quarters.Skip(1), c => Assert.Equal(0, c.FromUtc.ToLocalTime().Minute % 15));
        var second = quarters[1];
        int from = (int)(second.FromUtc - start).TotalMinutes;
        Assert.Equal(40 * GB + (from + 7) * GB, second.Cache); // the average of its 15 minutes
        Assert.Equal(now, quarters[^1].ToUtc);
        // A reading with no RAM size or no lists is not kept; the ring keeps 25 hours.
        ram.Add(now.AddMinutes(1), 0, 1, 1);
        ram.Add(now.AddMinutes(2), total, 0, 0);
        Assert.Equal(90, ram.Count);
        for (int i = 0; i < 2000; i++) ram.Add(now.AddMinutes(10 + i), total, GB, GB);
        Assert.Equal(RamHistory.Keep, ram.Count);
        Assert.Equal(now.AddMinutes(10 + 2000 - RamHistory.Keep).AddSeconds(-50), ram.At(0).MinuteUtc);
    }
}
