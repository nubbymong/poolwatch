using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// A pool sampled every 10 seconds, with PoolWatch's own cache clears in it, read by the detector and the incident manager
/// as the pipeline does (the clears it holds at each sample). <paramref name="seesClears"/> false reads the same samples
/// as if the clears were not PoolWatch's (the detector before it knew its own impact).
/// </summary>
internal sealed class ClearWorld : IDisposable
{
    public static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    public const long MB = Settings.MB, GB = 1024 * MB;

    private readonly bool _seesClears;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-ownclear-" + Guid.NewGuid().ToString("N"));
    /// <summary>How far apart the samples are (<see cref="Interval"/>, the default sampling, unless the world says otherwise).</summary>
    public readonly TimeSpan Step;
    public readonly History History;
    public readonly Detector Detector;
    public readonly IncidentManager Incidents;
    public readonly List<ClearRecord> Clears = [];
    /// <summary>Each sample's alerts and the statuses after it, in order.</summary>
    public readonly List<List<Alert>> Alerts = [];
    public readonly List<Dictionary<PoolTag, TagStatus>> Status = [];
    public readonly List<bool> Holding = [];
    /// <summary>The growth incidents open after each sample, by tag.</summary>
    public readonly List<HashSet<PoolTag>> Open = [];
    public int Index;

    public ClearWorld(Settings settings, bool seesClears = true, TimeSpan? step = null)
    {
        _seesClears = seesClears;
        Step = step ?? Interval;
        History = History.ForInterval(Step.TotalSeconds);
        Detector = new Detector(settings);
        Incidents = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
    }

    public DateTime Last => T0 + Step * (Index - 1);

    /// <summary>One sample: the committed non-paged total and each tag's (non-paged) bytes; then the detector and incidents.</summary>
    public void Sample(long nonPagedTotal, params (string Tag, long Bytes)[] tags) =>
        Sample(nonPagedTotal, tags.Select(t => (t.Tag, t.Bytes, 1u, 0u)).ToArray());

    /// <summary>The same, with each tag's allocation and free counters.</summary>
    public void Sample(long nonPagedTotal, params (string Tag, long Bytes, uint Allocs, uint Frees)[] tags)
    {
        var now = T0 + Step * Index++;
        var entries = tags.Select(t => new PoolTagEntry(PoolTag.Parse(t.Tag), 0, 0, 0, t.Allocs, t.Frees, (ulong)t.Bytes)).ToList();
        History.Add(new Sample(now, new PoolTotals(1UL << 30, (ulong)nonPagedTotal, 0, 0), entries, []));
        var clears = _seesClears ? new OwnClears(Clears, Settle) : OwnClears.None;
        var alerts = Detector.Evaluate(History, clears);
        Incidents.Process(History, Detector, alerts, now);
        Alerts.Add(alerts);
        Status.Add(Detector.Status.ToDictionary());
        Holding.Add(Detector.Holding);
        Open.Add(Incidents.Open.Select(i => i.Tag).ToHashSet());
    }

    /// <summary>PoolWatch clears the cache a second after the newest sample, as the clearer does (it records the pool then).</summary>
    public void Clear() => Clears.Add(new ClearRecord
    {
        TimeUtc = Last.AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = History.Totals[^1],
    });

    /// <summary>The index of the first sample at or after a clear plus its settle time: the one its result is measured at.</summary>
    public static int MeasuredAt(int clearAfter) => clearAfter + 1 + (int)Math.Ceiling((Settle.TotalSeconds - 9) / Interval.TotalSeconds);

    /// <summary><see cref="MeasuredAt"/> at this world's sampling (the clear runs a second after a sample).</summary>
    public int MeasuredAfter(int clearAfter) => clearAfter + 1 + (int)Math.Ceiling((Settle.TotalSeconds - (Step.TotalSeconds - 1)) / Step.TotalSeconds);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class OwnClearTests
{
    private const long MB = ClearWorld.MB, GB = ClearWorld.GB;
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");

    /// <summary>What a tag's status says, without its size now and its change since the history began (they include the clear's drop).</summary>
    private static (TagState, DateTime?, long, long, long)? Said(Dictionary<PoolTag, TagStatus> status, PoolTag tag) =>
        status.TryGetValue(tag, out var s) ? (s.State, s.StateSinceUtc, s.Delta1m, s.Delta10m, s.Delta60m) : null;

    private static (double, double, AllocShape) Rates(TagStatus s) => (Math.Round(s.Rates.AllocsPerMin, 6), Math.Round(s.Rates.FreesPerMin, 6), s.Rates.Shape);

    // ------------------------------------------------------------------ the refill after a clear

    [Theory]
    [InlineData(150)] // the refill raises nothing
    [InlineData(170)] // nor does one just under the 10-minute rule's 200 MB: a clear never lowers a rule's bar
    [InlineData(185)]
    [InlineData(300)] // the same growth alerts as the same curve without a clear: from the clear's level, not its dip
    public void TheRefillAfterAClearRaisesNothingTheSameCurveWithoutAClearWouldNot(int refillMb)
    {
        // Reff holds 600 MB; the clear drops it to 100 MB and the cache refills it to 250 MB by the time the clear is
        // measured, then by refillMb over 8 minutes. Back drops to 300 MB and is all back by then; Spike rises from 200 MB to
        // 400 MB and is back at 200 MB by then (neither is a release nor growth). Each allocates 6,000
        // times a minute and frees 600 times, and the clear frees 50,000 of Reff's at once. "The same curve without a
        // clear" holds the levels the clear left and then moves the same way. Every rule is judged over the clock's window
        // with its own bytes, so the clear raises exactly what that curve raises, from the same onset, in the same words.
        const int clearAfter = 119;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long WithClear(int i) => i <= clearAfter ? 600 * MB
            : i < measured ? 100 * MB + (i - clearAfter - 1) * 150 * MB / (measured - clearAfter - 1)
            : 250 * MB + Math.Min(i - measured, 48) * refillMb * MB / 48;
        long WithoutClear(int i) => i < measured ? 250 * MB : WithClear(i);
        long Back(int i) => i > clearAfter && i < measured ? 300 * MB : 600 * MB;
        long Spike(int i) => i > clearAfter && i < measured ? 400 * MB : 200 * MB;
        static uint Allocs(int i) => (uint)(1000 * i);
        static uint Frees(int i) => (uint)(100 * i);
        uint ReffFrees(int i, bool cleared) => Frees(i) + (cleared && i > clearAfter ? 50_000u : 0);

        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        using var c = new ClearWorld(new Settings(), seesClears: false);
        for (int i = 0; i < 300; i++)
        {
            a.Sample(GB, ("Reff", WithClear(i), Allocs(i), ReffFrees(i, true)), ("Back", Back(i), Allocs(i), Frees(i)), ("Spik", Spike(i), Allocs(i), Frees(i)));
            b.Sample(GB, ("Reff", WithoutClear(i), Allocs(i), ReffFrees(i, false)), ("Back", 600 * MB, Allocs(i), Frees(i)), ("Spik", 200 * MB, Allocs(i), Frees(i)));
            c.Sample(GB, ("Reff", WithClear(i), Allocs(i), ReffFrees(i, true)), ("Back", Back(i), Allocs(i), Frees(i)));
            if (i == clearAfter) { a.Clear(); c.Clear(); }
        }
        Assert.Equal(measured, a.History.IndexAtOrAfter(a.Clears[0].TimeUtc + ClearWorld.Settle));
        var back = PoolTag.Parse("Back");
        var spike = PoolTag.Parse("Spik");
        for (int i = 0; i < 300; i++)
        {
            Assert.Equal(b.Alerts[i], a.Alerts[i]);
            Assert.Equal(Said(b.Status[i], Reff), Said(a.Status[i], Reff));
            Assert.Equal(Said(b.Status[i], back), Said(a.Status[i], back));
            Assert.Equal(Said(b.Status[i], spike), Said(a.Status[i], spike));
            if (i > measured + 1) Assert.Equal(Rates(b.Status[i][Reff]), Rates(a.Status[i][Reff])); // the clear's frees are its own
        }
        Assert.Equal(AllocShape.LeakShape, a.Status[measured + 6][Reff].Rates.Shape);
        Assert.Equal(refillMb > 200, a.Alerts.SelectMany(x => x).Any(x => x.Kind == AlertKind.Growth));
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        int First(ClearWorld w) => w.Alerts.FindIndex(x => x.Any(y => y.Kind == AlertKind.Growth));
        if (refillMb > 200)
        {
            Assert.Equal(First(b), First(a));
            Assert.Equal("grew > 200 MB in 10 min", a.Alerts[First(a)].Single().Rule);
        }

        // Read as if the clear were not PoolWatch's, the same samples say Reff was released, and grew from the dip.
        var seen = c.Alerts.SelectMany(x => x).ToList();
        Assert.Contains(seen, x => x.Kind == AlertKind.Release);
        Assert.Contains(seen, x => x.Kind == AlertKind.Growth && x.Bytes > refillMb * MB);
    }

    [Fact]
    public void TheNonPagedTotalsRefillAfterAClearIsMeasuredFromItsLevelNotItsDip()
    {
        // The non-paged total holds 6 GB; the clear drops it to 5.2 GB, it is back to 5.26 GB when measured and rises 80 MB
        // more in 8 minutes: under the 100 MB in 10 minutes the rule needs, which a clear never lowers. Ten minutes after the
        // clear, the span starts in its dip: measured from there, the rise would be 140 MB.
        const int clearAfter = 119;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Total(int i) => i <= clearAfter ? 6 * GB
            : i < measured ? 6 * GB - 800 * MB + (i - clearAfter - 1) * 60 * MB / (measured - clearAfter - 1)
            : 6 * GB - 740 * MB + Math.Min(i - measured, 48) * 80 * MB / 48;
        long WithoutClear(int i) => i < measured ? 6 * GB - 740 * MB : Total(i);

        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        using var c = new ClearWorld(new Settings(), seesClears: false);
        for (int i = 0; i < 260; i++)
        {
            a.Sample(Total(i), ("MmSt", Total(i) - 4 * GB));
            b.Sample(WithoutClear(i), ("MmSt", WithoutClear(i) - 4 * GB));
            c.Sample(Total(i), ("MmSt", Total(i) - 4 * GB));
            if (i == clearAfter) { a.Clear(); c.Clear(); }
        }
        Assert.Empty(b.Alerts.SelectMany(x => x));
        Assert.Empty(a.Alerts.SelectMany(x => x));
        Assert.Contains(c.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.NonPagedTotal);
    }

    [Fact]
    public void AClearsDropLowersAnIncidentsHighOnceSoItClosesAsWithoutTheClear()
    {
        // Plat grows 1.26 GB in 30 minutes and then holds; the hour rule keeps firing on the plateau, with no new rise. A
        // clear on the plateau frees 100 MB of it: the incident still closes ten minutes after the last real rise, as it
        // does where no clear ran (the clear's drop lowers its high once, not again at every sample).
        const int flatFrom = 180, clearAfter = 200;
        long Plat(int i) => 100 * MB + Math.Min(i, flatFrom) * 7 * MB;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        var plat = PoolTag.Parse("Plat");
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Plat", Plat(i) - (i > clearAfter ? 100 * MB : 0)));
            b.Sample(GB, ("Plat", Plat(i)));
            if (i == clearAfter) a.Clear();
        }
        int ClosedAt(ClearWorld w) => w.Open.FindIndex(w.Open.FindIndex(o => o.Contains(plat)), o => !o.Contains(plat));
        Assert.Equal(flatFrom + 60, ClosedAt(b));
        Assert.Equal(ClosedAt(b), ClosedAt(a));
        Assert.Contains(a.Alerts[ClosedAt(a) - 1], x => x.Window == TimeSpan.FromHours(1)); // the hour rule still fired
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
    }

    [Fact]
    public void AnIncidentsGrowthCountsFromTheClearsLevelNotItsDip()
    {
        // Over grows 300 MB and holds at 800 MB (its incident closes); a clear drops it to 400 MB, it is at 450 MB when the
        // clear is measured, then grows to 1 GB and the incident re-opens. Its growth is the 550 MB from the clear's level,
        // not 600 MB from the dip.
        const int clearAfter = 299;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Over(int i) => i < 60 ? 500 * MB : i < 120 ? 500 * MB + (i - 59) * 5 * MB : i <= clearAfter ? 800 * MB
            : i < measured ? 400 * MB + (i - clearAfter - 1) * 50 * MB / (measured - clearAfter - 1)
            : 450 * MB + Math.Min(i - measured, 90) * 550 * MB / 90;
        using var a = new ClearWorld(new Settings());
        using var c = new ClearWorld(new Settings(), seesClears: false);
        for (int i = 0; i <= measured + 90; i++)
        {
            a.Sample(GB, ("Over", Over(i)));
            c.Sample(GB, ("Over", Over(i)));
            if (i == clearAfter) { a.Clear(); c.Clear(); }
        }
        var over = PoolTag.Parse("Over");
        var inc = Assert.Single(a.Incidents.All, i => i.Tag == over);
        Assert.True(inc.IsOpen);
        Assert.Equal(1, inc.Reopened);
        Assert.InRange(inc.Growth, 500 * MB, 550 * MB); // from its onset just above the clear's level
        Assert.True(inc.OnsetUtc >= a.History.Times[measured], $"onset {inc.OnsetUtc:HH:mm:ss}");
        Assert.Equal(0, inc.ClearedBytes); // the clear ran before this growth began: it freed none of it
        Assert.Null(inc.ClearedAtUtc);
        // Read as if the clear were not PoolWatch's, the growth is measured from the dip.
        Assert.Contains(c.Incidents.All, i => i.Tag == over && i.Kind == IncidentKind.Growth && i.Growth > 560 * MB);
    }

    // ------------------------------------------------------------------ a real leak across clears

    [Fact]
    public void ARealLeakStillRaisesAcrossOneAndAcrossSeveralHourlyClears()
    {
        // Three leaks and the cache, with a clear every hour for over three hours:
        // - "Leak" grows 22 MB a minute (just over the 200 MB in 10 minutes rule) and no clear frees any of it;
        // - "Part" grows 40 MB a minute and each clear frees 500 MB of it (part of it is held by the cache);
        // - "Reff" is the cache: each clear drops it from 800 MB to 100 MB and it refills over the hour, too slowly to alert.
        // Each leak's incident stays open across every clear; Reff never opens one.
        var settings = new Settings { IncidentMaxMinutes = 600 };
        int[] clearsAfter = [359, 719, 1079];
        long Cleared(int i) => clearsAfter.Count(k => i > k);
        long Since(int i) => clearsAfter.Where(k => i > k).Select(k => (long)(i - k)).DefaultIfEmpty(long.MaxValue).Min();
        (string, long)[] Tags(int i) =>
        [
            ("Leak", 200 * MB + i * 22 * MB / 6),
            ("Part", GB + i * 40 * MB / 6 - Cleared(i) * 500 * MB),
            ("Reff", Since(i) == long.MaxValue ? 800 * MB : 100 * MB + Math.Min(Since(i), 359) * 700 * MB / 360),
        ];

        using var a = new ClearWorld(settings);
        using var c = new ClearWorld(settings, seesClears: false);
        for (int i = 0; i < 1300; i++)
        {
            a.Sample(GB, Tags(i));
            c.Sample(GB, Tags(i));
            if (clearsAfter.Contains(i)) { a.Clear(); c.Clear(); }
        }

        foreach (var tag in new[] { "Leak", "Part" }.Select(PoolTag.Parse))
        {
            int opened = a.Open.FindIndex(o => o.Contains(tag));
            Assert.InRange(opened, 0, clearsAfter[0]);
            Assert.All(a.Open.Skip(opened), o => Assert.Contains(tag, o)); // never closed, across every clear
            var inc = Assert.Single(a.Incidents.All, i => i.Tag == tag);
            Assert.Equal(0, inc.Reopened);
        }
        Assert.DoesNotContain(a.Incidents.All, i => i.Tag == Reff);
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);

        // Read as if the clears were not PoolWatch's, Part's incident closes after a clear (the dip hid its growth) and the
        // cache is said to be released.
        var part = PoolTag.Parse("Part");
        int first = c.Open.FindIndex(o => o.Contains(part));
        Assert.Contains(c.Open.Skip(first), o => !o.Contains(part));
        Assert.Contains(c.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release && x.Tag == Reff);
    }

    [Fact]
    public void ARisingNonPagedTotalStillRaisesAcrossHourlyClears()
    {
        // The non-paged total rises 16 MB a minute from 5.5 GB (the rule: over 5 GB and 100 MB in 10 minutes), carried by
        // NpLk, and each hourly clear frees 300 MB of it. With each clear's drop added back the rise stays over the rule
        // (the clear's 2 minutes are the clear's), so the rule fires at every sample but the clears' and the system
        // incident stays open across every clear.
        var settings = new Settings { IncidentMaxMinutes = 600 };
        int[] clearsAfter = [359, 719];
        long NpLk(int i) => GB + i * 16 * MB / 6 - clearsAfter.Count(k => i > k) * 300 * MB;
        using var a = new ClearWorld(settings);
        using var c = new ClearWorld(settings, seesClears: false);
        for (int i = 0; i < 1000; i++)
        {
            a.Sample(4 * GB + 512 * MB + NpLk(i), ("NpLk", NpLk(i)));
            c.Sample(4 * GB + 512 * MB + NpLk(i), ("NpLk", NpLk(i)));
            if (clearsAfter.Contains(i)) { a.Clear(); c.Clear(); }
        }
        var system = Assert.Single(a.Incidents.All, i => i.Kind == IncidentKind.NonPagedTotal);
        Assert.True(system.IsOpen);
        Assert.Equal(0, system.Reopened);
        int opened = a.Alerts.FindIndex(x => x.Any(y => y.Kind == AlertKind.NonPagedTotal));
        Assert.InRange(opened, 0, clearsAfter[0]);
        Assert.All(a.Open.Skip(opened), o => Assert.NotEmpty(o));
        for (int i = opened; i < 1000; i++)
            if (!a.Holding[i]) Assert.Contains(a.Alerts[i], x => x.Kind == AlertKind.NonPagedTotal && x.Tag == PoolTag.Parse("NpLk"));

        // Read as if the clears were not PoolWatch's, the dip hides the rise for ten minutes and the incident closes.
        Assert.Contains(c.Incidents.All, i => i.Kind == IncidentKind.NonPagedTotal && (i.Reopened > 0 || !i.IsOpen));
    }

    // ------------------------------------------------------------------ a drop with no clear behind it

    [Fact]
    public void ADropWithNoClearBehindItIsStillReported()
    {
        // Gone is released (500 MB to 100 MB: its programs exited) four minutes before a clear that frees 300 MB of Reff and
        // trims 5 MB of what Gone still holds; then, after the clear is measured, Reff drops 200 MB more from the level the
        // clear left. Both drops are releases, and read as they are where no clear ran ("plain": Gone at 100 MB and Reff at
        // the clear's level all along); the clear's own drops are not.
        const int clearAfter = 120;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Gone(int i, bool cleared) => (i < clearAfter - 24 ? 500 * MB : 100 * MB) - (cleared && i > clearAfter ? 5 * MB : 0);
        long ReffAt(int i, bool cleared) => cleared && i <= clearAfter ? 600 * MB : i < measured + 18 ? 250 * MB : 50 * MB;
        using var a = new ClearWorld(new Settings());
        using var plain = new ClearWorld(new Settings());
        for (int i = 0; i < 200; i++)
        {
            a.Sample(GB, ("Gone", Gone(i, true)), ("Reff", ReffAt(i, true)));
            plain.Sample(GB, ("Gone", Gone(i, false)), ("Reff", ReffAt(i, false)));
            if (i == clearAfter) a.Clear();
        }
        var gone = PoolTag.Parse("Gone");
        var releases = a.Alerts.SelectMany(x => x).Where(x => x.Kind == AlertKind.Release).ToList();
        Assert.Equal(new[] { (gone, 400 * MB), (Reff, 200 * MB) }, releases.Select(r => (r.Tag, r.Bytes)));
        Assert.Equal(plain.Alerts.SelectMany(x => x).Where(x => x.Kind == AlertKind.Release), releases);
        // Each tag reads as it does where no clear ran, over its whole window: Gone stays released, its fall still in its
        // changes; Reff is never released by the clear's own drop. (While Reff's release window starts inside the clear's
        // interval, it is released since the clear's measurement, the first sample there that is its own, where the curve
        // with no clear says the window's start: the same flat level either way.)
        static (TagState, long, long, long)? Changes(Dictionary<PoolTag, TagStatus> s, PoolTag t) =>
            s.TryGetValue(t, out var x) ? (x.State, x.Delta1m, x.Delta10m, x.Delta60m) : null;
        for (int i = 0; i < 200; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(Said(plain.Status[i], gone), Said(a.Status[i], gone));
                Assert.Equal(Changes(plain.Status[i], Reff), Changes(a.Status[i], Reff));
            }
        Assert.Equal(TagState.Released, a.Status[measured + 1][gone].State);
        Assert.Equal(-400 * MB, a.Status[measured + 1][gone].Delta10m);
        Assert.Equal(TagState.Normal, a.Status[measured + 1][Reff].State);
        Assert.Equal(Said(plain.Status[measured + 18], Reff), Said(a.Status[measured + 18], Reff));
        Assert.Equal(TagState.Released, a.Status[measured + 18][Reff].State);
        // Reff's release record: the 600 MB it really held when its window began, down to 50 MB; PoolWatch's own clear freed
        // 350 MB of that, and 200 MB fell on its own. Its report says each part as what it was.
        var inc = Assert.Single(a.Incidents.All, x => x.Tag == Reff);
        var release = inc.Release!;
        Assert.Equal((600 * MB, 50 * MB, 350 * MB, 200 * MB), (release.FromBytes, release.ToBytes, release.ClearedBytes, release.Dropped));
        var clear = a.Clears[0].TimeUtc;
        Assert.Equal(clear, release.ClearedAtUtc);
        Assert.True(release.FromUtc <= a.History.Times[clearAfter]); // its window began before the clear
        a.Incidents.Finish(a.History, a.Last);
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        string span = $"between {Fmt.Clock(release.FromUtc)} and {Fmt.Clock(release.TimeUtc)}";
        Assert.Contains($"- **Released**: 600 MB -> 50 MB {span}; PoolWatch's own cache clear at {Fmt.Clock(clear)} freed 350 MB of it and 200 MB fell on its own.", md);
        Assert.Contains($"`Reff` fell 200 MB on its own (600 MB -> 50 MB, of which PoolWatch's own cache clear at {Fmt.Clock(clear)} freed 350 MB) {span}.", md);
        Assert.Contains($"# `Reff` (", md);
        Assert.Contains("dropped 200 MB at", md); // the headline: what fell on its own
        // Gone's release had no clear in its span: said as before.
        var gone2 = Assert.Single(a.Incidents.All, x => x.Tag == gone);
        Assert.Equal(0, gone2.Release!.ClearedBytes);
        string gmd = File.ReadAllText(Path.Combine(gone2.Dir, "report.md"));
        Assert.Contains($"- **Released**: 500 MB -> 100 MB between {Fmt.Clock(gone2.Release.FromUtc)} and {Fmt.Clock(gone2.Release.TimeUtc)}.", gmd);
        Assert.Contains($"`Gone` fell 400 MB (500 MB -> 100 MB) between ", gmd);
    }

    // ------------------------------------------------------------------ no clear in the window: exactly as before

    /// <summary>Clears that are no part of any window: a day earlier, one that failed in the middle, one dated after the last sample.</summary>
    private static List<ClearRecord> ClearsOutside(DateTime t0, DateTime middle, DateTime end)
    {
        ClearRecord At(DateTime t, string? error = null) => new()
        {
            TimeUtc = t, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default, After = error is null ? default(PoolTotals) : null, Error = error,
        };
        return [At(t0.AddDays(-1)), At(middle, "access denied"), At(end.AddMinutes(5))];
    }

    [Fact]
    public void WithNoClearInTheWindowEveryFindingAlertAndIncidentIsExactlyAsBefore()
    {
        // The detection test's scenario (growth, then a release) and the incident test's (growth, quiet, release), read with
        // no clears and with clears outside every window: the same alerts, statuses and incidents at every sample.
        var ci = new Settings { SampleIntervalSeconds = 2, GrowthRules = [new(1, 3 * Settings.MB)], ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
        var w = new World();
        var outside = new OwnClears(ClearsOutside(World.T0, World.T0.AddSeconds(60), World.T0.AddSeconds(90)), TimeSpan.FromMinutes(2));
        var plain = new Detector(ci);
        var withClears = new Detector(ci);
        long v = 10 * Settings.MB;
        void Step()
        {
            w.Sample(("Reff", v), ("File", 40 * Settings.MB - v));
            Assert.Equal(plain.Evaluate(w.History), withClears.Evaluate(w.History, outside));
            Assert.Equal(plain.Status.OrderBy(x => x.Key.Raw), withClears.Status.OrderBy(x => x.Key.Raw));
            Assert.False(withClears.Holding);
        }
        for (int i = 0; i < 20; i++) Step();
        for (int i = 0; i < 20; i++) { v += 400_000; Step(); }
        for (int i = 0; i < 5; i++) { v -= 1_000_000; Step(); }
        Assert.Equal(TagState.Released, withClears.Status[Reff].State);

        string dir = Path.Combine(Path.GetTempPath(), "pw-ownclear-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 0.5, ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
            var world = new World();
            var mgrPlain = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(Path.Combine(dir, "a")), world.Registry) { RewriteInterval = TimeSpan.Zero };
            var mgrClears = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(Path.Combine(dir, "b")), world.Registry) { RewriteInterval = TimeSpan.Zero };
            var detPlain = new Detector(settings);
            var detClears = new Detector(settings);
            var around = new OwnClears(ClearsOutside(World.T0, World.T0.AddSeconds(50), World.T0.AddSeconds(126)), TimeSpan.FromMinutes(2));
            world.AddProcess(4, 0, "System");
            world.AddProcess(100, 4, "leaker.exe");
            long file = 50 * Settings.MB;
            object Incidents(IncidentManager m) => string.Join(";", m.All.Select(i =>
                $"{i.Kind} {i.Tag} {i.OpenedUtc:O} {i.ClosedUtc:O} {i.OnsetUtc:O} {i.EndUtc:O} {i.Growth} {i.HighestSeenBytes} {i.LastRiseUtc:O} {i.Alerts.Count} {i.Retention is null} {i.Release is null}"));
            void Tick()
            {
                world.Sample(("File", file));
                var now = world.Now - world.Interval;
                var x = detPlain.Evaluate(world.History);
                var y = detClears.Evaluate(world.History, around);
                Assert.Equal(x, y);
                mgrPlain.Process(world.History, detPlain, x, now);
                mgrClears.Process(world.History, detClears, y, now);
                Assert.Equal(Incidents(mgrPlain), Incidents(mgrClears));
            }
            for (int i = 0; i < 15; i++) Tick();
            for (int i = 0; i < 20; i++) { world.Io(100, other: 2000, handles: 2000); file += 800_000; Tick(); }
            for (int i = 0; i < 25; i++) Tick();
            file -= 16 * Settings.MB;
            for (int i = 0; i < 3; i++) Tick();
            Assert.NotEmpty(mgrClears.All);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ------------------------------------------------------------------ while a clear is measured

    [Fact]
    public void AClearStillBeingMeasuredHoldsFindingsSteady()
    {
        // Grow climbs 30 MB a minute for 15 minutes (an incident opens), then holds; nine minutes later PoolWatch clears
        // the cache and Grow drops 300 MB. Until the clear is measured nothing changes: no alert, the same statuses, and the
        // incident stays open although its ten quiet minutes pass meanwhile. Once measured, the drop is the clear's (no
        // release) and the quiet incident closes.
        const int flatFrom = 90, clearAfter = flatFrom + 54;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Grow(int i) => 200 * MB + Math.Min(i, flatFrom) * 5 * MB - (i > clearAfter ? 300 * MB : 0);
        var grow = PoolTag.Parse("Grow");
        using var a = new ClearWorld(new Settings());
        for (int i = 0; i < measured + 30; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            if (i == clearAfter) a.Clear();
        }
        Assert.Contains(grow, a.Open[clearAfter]);
        for (int i = clearAfter + 1; i < measured; i++)
        {
            Assert.True(a.Holding[i]);
            Assert.Empty(a.Alerts[i]);
            Assert.Equal(a.Status[clearAfter], a.Status[i]);
            Assert.Contains(grow, a.Open[i]); // its ten quiet minutes end inside the interval: held open
        }
        Assert.False(a.Holding[measured]);
        Assert.DoesNotContain(grow, a.Open[measured]); // measured: the growth did stop
        Assert.Equal(TagState.Normal, a.Status[measured][grow].State);
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        var inc = Assert.Single(a.Incidents.All);
        Assert.Contains(inc.Timeline, t => t.TimeUtc == a.History.Times[measured] && t.Text.StartsWith("closed: no new rise", StringComparison.Ordinal));
    }

    [Fact]
    public void NoRetentionIsFoundWhileAClearIsMeasured()
    {
        // The incident test's scenario: growth, quiet (the incident closes), then File keeps its memory a minute after
        // leaker.exe went quiet (retention). A clear that frees none of it runs just before that is found: it is found once
        // the clear is measured, not before.
        var settle = TimeSpan.FromSeconds(20);
        (int RetainedAt, List<bool> Holding) Run(int? clearAfter)
        {
            string dir = Path.Combine(Path.GetTempPath(), "pw-ownclear-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 1, ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
                var w = new World();
                var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry) { RewriteInterval = TimeSpan.Zero };
                var det = new Detector(settings);
                var clears = new List<ClearRecord>();
                var holding = new List<bool>();
                w.AddProcess(4, 0, "System");
                w.AddProcess(100, 4, "leaker.exe");
                long file = 50 * Settings.MB;
                int retainedAt = -1;
                for (int i = 0; i < 90; i++)
                {
                    if (i >= 15 && i < 35) { w.Io(100, other: 2000, handles: 2000); file += 800_000; }
                    w.Sample(("File", file));
                    if (i == 14 || (i >= 15 && i < 35 && i % 5 == 4)) w.ScanHandles();
                    var now = w.Now - w.Interval;
                    mgr.Process(w.History, det, det.Evaluate(w.History, new OwnClears(clears, settle)), now);
                    holding.Add(det.Holding);
                    if (retainedAt < 0 && mgr.All.Any(x => x.Retention is not null)) retainedAt = i;
                    if (i == clearAfter) Assert.Empty(mgr.Open); // closed before the clear: only the retention is held
                    if (i == clearAfter) clears.Add(new ClearRecord { TimeUtc = now.AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default });
                }
                return (retainedAt, holding);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        var (plain, _) = Run(null);
        Assert.InRange(plain, 36, 80);
        var (cleared, holding) = Run(plain - 3);
        int measured = holding.FindIndex(plain - 2, h => !h);
        Assert.True(measured > plain, $"measured at {measured}, retention at {plain}");
        Assert.All(holding.Skip(plain - 2).Take(measured - (plain - 2)), h => Assert.True(h));
        Assert.Equal(measured, cleared);
    }

    // ------------------------------------------------------------------ the clears placed on the samples

    [Fact]
    public void AClearsIntervalRunsFromTheLastSampleBeforeItToTheOneThatMeasuredIt()
    {
        using var w = new ClearWorld(new Settings());
        for (int i = 0; i < 10; i++) w.Sample(GB, ("Reff", 600 * MB));
        w.Clear();
        var clears = new OwnClears(w.Clears, ClearWorld.Settle);
        Assert.Same(w.Clears[0], clears.Measuring(w.Clears[0].TimeUtc));
        for (int i = 0; i < 12; i++) w.Sample(GB, ("Reff", 100 * MB));
        Assert.NotNull(clears.Measuring(w.Last));         // 119 s after the clear
        Assert.Equal(new ClearSpan(9, w.History.Count), Assert.Single(clears.SpansIn(w.History).Spans)); // not measured yet
        w.Sample(GB, ("Reff", 250 * MB));                  // 129 s: measured
        Assert.Null(clears.Measuring(w.Last));
        var spans = clears.SpansIn(w.History);
        var span = Assert.Single(spans.Spans);
        Assert.Equal(new ClearSpan(9, 22), span);
        Assert.Equal(w.Clears[0].TimeUtc, spans.TimeOf(span));
        Assert.Null(spans.TimeOf(new ClearSpan(9, 21)));             // not one of its spans
        Assert.Equal(22, ClearWorld.MeasuredAt(9));
        Assert.False(span.Holds(9));
        Assert.True(span.Holds(10));
        Assert.True(span.Holds(21));
        Assert.False(span.Holds(22));
        Assert.Equal((9, 22, 22), (spans.Start(9), spans.Start(10), spans.Start(22)));
        Assert.Equal(MB, ClearSpan.Drop(2 * MB, MB));
        Assert.Equal(0, ClearSpan.Drop(MB, 2 * MB));                // it rose across the clear: the growth is the series'

        // The stitch: the drop is added back from the measurement on; the samples inside take the level before (fill) or
        // are left for the reader to skip.
        long[] Stitched(bool addBack, bool fill)
        {
            var s = w.History.TagSeries(Reff, 8);
            spans.Stitch(s, 8, addBack, fill);
            return s;
        }
        Assert.Equal([600 * MB, 600 * MB, .. Enumerable.Repeat(100 * MB, 12), 600 * MB], Stitched(addBack: true, fill: false));
        Assert.Equal([600 * MB, 600 * MB, .. Enumerable.Repeat(600 * MB, 12), 600 * MB], Stitched(addBack: true, fill: true));
        Assert.Equal([600 * MB, 600 * MB, .. Enumerable.Repeat(600 * MB, 12), 250 * MB], Stitched(addBack: false, fill: true));
        var fromInside = w.History.TagSeries(Reff, 12);
        spans.Stitch(fromInside, 12, fill: true);                    // starts inside: what was before is not in it
        Assert.Equal([.. Enumerable.Repeat(250 * MB, 10), 250 * MB], fromInside);
        // The spans that dropped a series: Reff fell 350 MB across it; a series that rose across it was not dropped.
        var dropped = new List<ClearSpan>();
        spans.Stitch(w.History.TagSeries(Reff, 8), 8, dropped: dropped);
        Assert.Equal([span], dropped);
        dropped.Clear();
        spans.Stitch([.. Enumerable.Range(0, 15).Select(i => (long)i * MB)], 8, dropped: dropped);
        Assert.Empty(dropped);

        // A clear pressed again while the first is measured: one interval, measured once both are.
        var again = new OwnClears([.. w.Clears, new ClearRecord { TimeUtc = w.History.Times[15].AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default }], ClearWorld.Settle);
        Assert.Equal(new ClearSpan(9, w.History.Count), Assert.Single(again.SpansIn(w.History).Spans));
        var sameSample = new OwnClears([.. w.Clears, new ClearRecord { TimeUtc = w.Clears[0].TimeUtc.AddSeconds(4), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default }], ClearWorld.Settle);
        var merged = sameSample.SpansIn(w.History);
        Assert.Equal(new ClearSpan(9, 22), Assert.Single(merged.Spans));
        Assert.Null(merged.TimeOf(merged.Spans[0]));                 // two clears in one: no one time to say
        var afterIt = new OwnClears([.. w.Clears, new ClearRecord { TimeUtc = w.History.Times[22].AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default }], ClearWorld.Settle);
        Assert.Equal([new ClearSpan(9, 22)], afterIt.SpansIn(w.History).Spans); // it ran after the newest sample: not yet
        w.Sample(GB, ("Reff", 260 * MB));
        var two = afterIt.SpansIn(w.History);
        Assert.Equal([new ClearSpan(9, 22), new ClearSpan(22, w.History.Count)], two.Spans); // starts where the first was measured
        Assert.Equal((w.Clears[0].TimeUtc, w.History.Times[22].AddSeconds(1)), (two.TimeOf(two.Spans[0])!.Value, two.TimeOf(two.Spans[1])!.Value));

        var failed = new ClearRecord { TimeUtc = w.Last, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default, Error = "access denied" };
        Assert.Null(new OwnClears([failed], ClearWorld.Settle).Measuring(w.Last));
        Assert.Empty(new OwnClears([failed], ClearWorld.Settle).SpansIn(w.History).Spans);
        Assert.Empty(OwnClears.None.SpansIn(w.History).Spans);
    }

    // ------------------------------------------------------------------ one baseline across a clear

    [Fact]
    public void ALeaksRowItsAlertItsIncidentAndTheEvidenceCountTheGrowthBeforeTheClearToo()
    {
        // Toke (a pack's tag, so the known-issue evidence measures it too) leaks 40 MB a minute and a clear frees 500 MB of
        // it. Five minutes after the clear is measured, its change over the hour, the hour rule's growth and the evidence's
        // are one figure: the hour's change with the clear's drop added back (its interval is the clear's), far more than
        // the growth since the clear. Toke's growth in the clear's 13 samples is the clear's too, so the hour holds 347
        // samples of it. Its incident counts the same growth, and says what the clear freed.
        const int clearAfter = 359;
        int measured = ClearWorld.MeasuredAt(clearAfter), at = measured + 30;
        long Toke(int i) => GB + i * 40 * MB / 6 - (i > clearAfter ? 500 * MB : 0);
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        for (int i = 0; i <= at; i++)
        {
            a.Sample(GB, ("Toke", Toke(i)));
            if (i == clearAfter) a.Clear();
        }
        var toke = PoolTag.Parse("Toke");
        long V(int i) { var (p, np) = a.History.TagAt(toke, i); return p + np; }
        long drop = V(clearAfter) - V(measured), expected = V(at) - V(at - 360) + drop;
        Assert.Equal(expected, a.Status[at][toke].Delta60m);
        Assert.Equal(expected, Assert.Single(a.Alerts[at], x => x.Tag == toke && x.Window == TimeSpan.FromHours(1)).Bytes);
        var evidence = KnowledgeEvidence.Gather(KnowledgeBase.Embedded(), a.History, a.Clears, ClearWorld.Settle,
            new RecommendationInputs { NowUtc = a.Last }, null, []);
        Assert.Equal(TimeSpan.FromHours(1), evidence.GrowthOver);
        Assert.Equal(expected, evidence.Growth[toke].NonPagedBytes);
        Assert.InRange(expected, (360 - 13) * 40 * MB / 6 - 4096, (360 - 13) * 40 * MB / 6 + 4096); // to the KB the history holds
        Assert.True(expected > V(at) - V(measured) + 1500 * MB);

        var inc = Assert.Single(a.Incidents.Open, x => x.Tag == toke);
        a.Incidents.Rank(a.History, inc, a.Last);
        Assert.Equal(drop, inc.ClearedBytes);
        Assert.Equal(V(at), inc.CurrentBytes);
        Assert.Equal(inc.PeakBytes - inc.CurrentBytes, inc.ClearedBytes); // still climbing: its peak is now, with the drop added back
        Assert.Equal(V(at) + drop - inc.StartBytes, inc.Growth);
        Assert.Equal(a.Clears[0].TimeUtc, inc.ClearedAtUtc);
        a.Incidents.Finish(a.History, a.Last);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        Assert.Equal(drop, json.RootElement.GetProperty("clearedBytes").GetInt64());
        Assert.Equal(a.Clears[0].TimeUtc, json.RootElement.GetProperty("clearedAtUtc").GetDateTime());
        var growthLine = Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith("- Growth:", StringComparison.Ordinal));
        Assert.EndsWith($"; now {Fmt.Bytes(V(at))}, after PoolWatch's own cache clear at {Fmt.Clock(a.Clears[0].TimeUtc)} freed {Fmt.Bytes(drop)} of it", growthLine);
    }

    [Fact]
    public void AnIncidentWithNoClearInItSaysNothingOfClears()
    {
        // A leak with no clear: its report's Growth line ends at its level now, and report.json says no clear freed any of it.
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        for (int i = 0; i < 200; i++) a.Sample(GB, ("Leak", GB + i * 40 * MB / 6));
        var inc = Assert.Single(a.Incidents.All);
        a.Incidents.Finish(a.History, a.Last);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith("- Growth:", StringComparison.Ordinal));
        Assert.EndsWith($"; now {Fmt.Bytes(inc.CurrentBytes)}", line);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        Assert.Equal(0, json.RootElement.GetProperty("clearedBytes").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("clearedAtUtc").ValueKind);
        Assert.Null(inc.OwnClearsFreed);
        // The rule's words with no clear, exactly as they always were.
        Assert.Equal("grew > 200 MB in 10 min", inc.Alerts.First(x => x.Window == TimeSpan.FromMinutes(10)).Rule);
        Assert.Equal("grew > 1.0 GB in 60 min", inc.Alerts.First(x => x.Window == TimeSpan.FromHours(1)).Rule);
    }

    [Fact]
    public void ALightClearThatTrimsAGrowingTagLeavesItsGrowthWhole()
    {
        // Grow climbs 6 MB a minute; 50 minutes in, a light clear trims 20 MB of it. Its changes and states read as for the
        // same tag pausing for the clear's interval with no clear ("paused"): the growth before the clear still counts, and
        // the trim is no fall.
        const int clearAfter = 300;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Grow(int i) => 100 * MB + i * MB;
        long Trimmed(int i) => Grow(i) - (i > clearAfter ? 20 * MB : 0);
        long Paused(int i) => i <= clearAfter ? Grow(i) : i < measured ? Grow(clearAfter) : Grow(i) - (Grow(measured) - Grow(clearAfter));
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 420; i++)
        {
            a.Sample(GB, ("Grow", Trimmed(i)));
            b.Sample(GB, ("Grow", Paused(i)));
            if (i == clearAfter) a.Clear();
        }
        var grow = PoolTag.Parse("Grow");
        for (int i = 0; i < 420; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(b.Alerts[i], a.Alerts[i]);
                Assert.Equal(Said(b.Status[i], grow), Said(a.Status[i], grow));
            }
        var s = a.Status[measured + 6][grow]; // a minute after the clear was measured
        Assert.Equal(TagState.Growing, s.State);
        Assert.Equal((47 * MB, 306 * MB), (s.Delta10m, s.Delta60m)); // not the 6 MB since the clear's level
    }

    /// <summary>A tag's level at the newest sample, as the history holds it (to the kilobyte).</summary>
    private static long Level(ClearWorld w, PoolTag tag) { var (p, np) = w.History.TagAt(tag, w.History.Count - 1); return p + np; }

    /// <summary>Which rules fired: the bytes a rule found can differ where a window started inside a clear's interval.</summary>
    private static List<(AlertKind, PoolTag, TimeSpan)> Fired(List<Alert> alerts) => alerts.Select(x => (x.Kind, x.Tag, x.Window)).ToList();

    /// <summary>What the alerts found, without their words (the non-paged rule's words give the total's real level now, which a clear lowers).</summary>
    private static List<(AlertKind, PoolTag, TimeSpan, long, DateTime)> Found(List<Alert> alerts) => alerts.Select(x => (x.Kind, x.Tag, x.Window, x.Bytes, x.OnsetUtc)).ToList();

    [Fact]
    public void AClearThatFreesNothingOfATagHidesNoFindingAboutIt()
    {
        // The non-paged total holds 6 GB; a program frees 300 MB of it (no clear), a minute later PoolWatch's clear frees
        // nothing (as a light clear can where the pool is not cache-held), and the pool then grows 150 MB over 6 minutes.
        // Every alert, state and change is what the same curve gives with no clear, except while the clear is measured: the
        // clear dropped nothing, and a window that starts inside it is measured from its measurement with the rule's own
        // bytes.
        const int fall = 60, clearAfter = 66;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Total(int i) => i < fall ? 6 * GB : 6 * GB - 300 * MB + Math.Clamp(i - measured, 0, 36) * 150 * MB / 36;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 200; i++)
        {
            a.Sample(Total(i), ("MmSt", Total(i) - 4 * GB));
            b.Sample(Total(i), ("MmSt", Total(i) - 4 * GB));
            if (i == clearAfter) a.Clear();
        }
        var mmst = PoolTag.Parse("MmSt");
        for (int i = 0; i < 200; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(b.Alerts[i], a.Alerts[i]);
                Assert.Equal(Said(b.Status[i], mmst), Said(a.Status[i], mmst));
            }
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        Assert.DoesNotContain(a.Alerts[measured + 36], x => x.Kind == AlertKind.NonPagedTotal); // 150 MB above the clear's level, 150 MB below 10 minutes ago
        Assert.Contains(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.NonPagedTotal);   // once the fall is out of the window: a real rise
    }

    [Fact]
    public void AClearThatFreesNothingOfATagGrowingThroughItHidesNoFindingAboutIt()
    {
        // MmSt (and with it the non-paged total, over 5 GB) grows 21 MB a minute, just over the 10-minute rule, and goes on
        // growing through a clear that frees none of it. Every rule fires at every sample but the clear's, as on the same
        // curve with no clear, except that a window that starts inside the clear's interval is measured from its measurement
        // (its changes are shorter for those 2 minutes), with the rule's own bytes: the 10-minute rule finds the growth there
        // later than with no clear, never sooner. The incident stays open throughout.
        const int clearAfter = 200;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Grow(int i) => GB + i * 21 * MB / 6;
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        using var b = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        for (int i = 0; i < 300; i++)
        {
            a.Sample(4 * GB + Grow(i), ("MmSt", Grow(i)));
            b.Sample(4 * GB + Grow(i), ("MmSt", Grow(i)));
            if (i == clearAfter) a.Clear();
        }
        var mmst = PoolTag.Parse("MmSt");
        var ten = (AlertKind.Growth, mmst, TimeSpan.FromMinutes(10));
        bool StartsInside(int i) => i - 60 > clearAfter && i - 60 < measured; // its 10-minute window starts inside the interval
        for (int i = 61; i < 300; i++)
            if (!a.Holding[i])
            {
                var fired = Fired(a.Alerts[i]);
                Assert.Equal(Fired(b.Alerts[i]).Where(x => x != ten || !StartsInside(i) || fired.Contains(ten)), fired);
                Assert.Contains((AlertKind.NonPagedTotal, mmst, TimeSpan.FromMinutes(10)), fired);
                Assert.Equal(StartsInside(i) ? (i - measured) * 21 * MB / 6 > 200 * MB : true, fired.Contains(ten));
                Assert.Equal(b.Status[i][mmst].State, a.Status[i][mmst].State);
            }
        int inside = measured + 50; // its 10-minute window starts inside the clear's interval
        long V(int i) => Grow(i);
        Assert.Equal(V(inside) - V(measured), a.Status[inside][mmst].Delta10m);
        Assert.Equal(V(inside) - V(inside - 60), b.Status[inside][mmst].Delta10m);
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        Assert.All(a.Open.Skip(61), o => Assert.Contains(mmst, o));
    }

    [Fact]
    public void TwoClearsInTheHourAddBothDropsBack()
    {
        // The size rule can clear every 30 minutes, so an hour can hold two clears. Reff's change over the hour is its
        // growth between them plus its growth since the second, each from the level a clear left; neither drop counts.
        const int c1 = 119, c2 = 299;
        int m1 = ClearWorld.MeasuredAt(c1), m2 = ClearWorld.MeasuredAt(c2);
        long ReffAt(int i) => i <= c1 ? 600 * MB
            : i < m1 ? 100 * MB
            : i <= c2 ? 250 * MB + (i - m1) * MB
            : i < m2 ? 100 * MB
            : 200 * MB + Math.Min(i - m2, 48) * MB;
        using var a = new ClearWorld(new Settings());
        for (int i = 0; i < 400; i++)
        {
            a.Sample(GB, ("Reff", ReffAt(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        for (int i = m2; i < 400; i++)
            Assert.Equal(ReffAt(c2) - ReffAt(m1) + ReffAt(i) - ReffAt(m2), a.Status[i][Reff].Delta60m);
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
    }

    // ------------------------------------------------------------------ leaks that clears partly free

    [Fact]
    public void ALeakOnlyTheTenMinuteRuleCatchesStaysOpenAcrossClearsThatFreePartOfIt()
    {
        // Part grows 22 MB a minute, just over the only rule here (200 MB in 10 minutes), and each hourly clear frees 500 MB
        // of it. A window that holds a clear's interval measures under 200 MB (the interval is the clear's, and the rule
        // keeps its own 200 MB), so the rule is quiet for over 11 minutes after each clear: the incident, rising when the
        // clear ran, counts its quiet minutes from the clear's measurement and stays open, never re-opening with a second
        // notice.
        var settings = new Settings { GrowthRules = [new(10, 200 * MB)], IncidentMaxMinutes = 600 };
        int[] clearsAfter = [359, 719];
        long Part(int i) => GB + i * 22 * MB / 6 - clearsAfter.Count(k => i > k) * 500 * MB;
        using var a = new ClearWorld(settings);
        for (int i = 0; i < 1000; i++)
        {
            a.Sample(GB, ("Part", Part(i)));
            if (clearsAfter.Contains(i)) a.Clear();
        }
        var part = PoolTag.Parse("Part");
        int opened = a.Open.FindIndex(o => o.Contains(part));
        Assert.InRange(opened, 0, clearsAfter[0]);
        Assert.All(a.Open.Skip(opened), o => Assert.Contains(part, o));
        Assert.Equal(0, Assert.Single(a.Incidents.All, x => x.Tag == part).Reopened);
        int next = a.Alerts.FindIndex(clearsAfter[0] + 1, x => x.Count > 0);
        Assert.True(next - clearsAfter[0] > 60, $"next alert at {next}"); // longer than its ten quiet minutes
        Assert.Equal("grew > 200 MB in 10 min", Assert.Single(a.Alerts[next]).Rule);
        Assert.True(a.Alerts[next][0].Bytes > 200 * MB);
    }

    [Fact]
    public void ALeakOnlyTheHourRuleCatchesStaysOpenAcrossHourlyClearsThatTrimIt()
    {
        // Trim grows 18 MB a minute (about 1.05 GB an hour: only the hour rule catches it) and each hourly clear frees 100 MB
        // of it. With the drop added back, the hour still holds over 1 GB of growth and the incident stays open; read as if
        // the clears were not PoolWatch's, each clear's drop takes the hour under 1 GB and the incident closes.
        var settings = new Settings { IncidentMaxMinutes = 600 };
        int[] clearsAfter = [359, 719, 1079];
        long Trim(int i) => GB + i * 3 * MB - clearsAfter.Count(k => i > k) * 100 * MB;
        using var a = new ClearWorld(settings);
        using var c = new ClearWorld(settings, seesClears: false);
        for (int i = 0; i < 1300; i++)
        {
            a.Sample(GB, ("Trim", Trim(i)));
            c.Sample(GB, ("Trim", Trim(i)));
            if (clearsAfter.Contains(i)) { a.Clear(); c.Clear(); }
        }
        var trim = PoolTag.Parse("Trim");
        int opened = a.Open.FindIndex(o => o.Contains(trim));
        Assert.InRange(opened, 0, clearsAfter[0]);
        Assert.All(a.Open.Skip(opened), o => Assert.Contains(trim, o));
        Assert.Equal(0, Assert.Single(a.Incidents.All, x => x.Tag == trim).Reopened);
        int first = c.Open.FindIndex(o => o.Contains(trim));
        Assert.Contains(c.Open.Skip(first), o => !o.Contains(trim));
    }

    // ------------------------------------------------------------------ incidents across a clear

    [Fact]
    public void AClosedIncidentReopensAfterAClearAsItDoesWithoutOne()
    {
        // Plat grows 1.26 GB in 30 minutes and holds (its incident closes); a clear frees 100 MB of it; later it grows 60 MB
        // more. From the level the clear left that is a new rise, and the incident re-opens at the same sample as on the
        // same curve with no clear.
        const int flatFrom = 180, clearAfter = 260, more = 300;
        long Plat(int i) => 100 * MB + Math.Min(i, flatFrom) * 7 * MB + Math.Clamp(i - more, 0, 6) * 10 * MB;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        var plat = PoolTag.Parse("Plat");
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Plat", Plat(i) - (i > clearAfter ? 100 * MB : 0)));
            b.Sample(GB, ("Plat", Plat(i)));
            if (i == clearAfter) a.Clear();
        }
        for (int i = 0; i < 330; i++) Assert.Equal(b.Open[i].Contains(plat), a.Open[i].Contains(plat));
        int reopened = b.Open.FindIndex(flatFrom + 61, o => o.Contains(plat));
        Assert.InRange(reopened, more, more + 6);
        Assert.Equal(1, Assert.Single(a.Incidents.All, x => x.Tag == plat).Reopened);
    }

    [Fact]
    public void AGrowthThatStopsAsAClearThatFreesNoneOfItRunsClosesAsWithoutTheClear()
    {
        // Grow climbs 30 MB a minute and stops just as PoolWatch clears the cache; the clear frees none of it. Its incident
        // closes ten minutes after its last rise, as where no clear ran: only a clear that dropped the tag could have hidden
        // a rise in its interval.
        const int stopAt = 120;
        long Grow(int i) => 100 * MB + Math.Min(i, stopAt) * 5 * MB;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        var grow = PoolTag.Parse("Grow");
        for (int i = 0; i < 240; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            b.Sample(GB, ("Grow", Grow(i)));
            if (i == stopAt) a.Clear();
        }
        int ClosedAt(ClearWorld w) => w.Open.FindIndex(w.Open.FindIndex(o => o.Contains(grow)), o => !o.Contains(grow));
        Assert.Equal(stopAt + 60, ClosedAt(b));
        Assert.Equal(ClosedAt(b), ClosedAt(a));
    }

    [Fact]
    public void AnIncidentClosedAtItsMaximumWhileAClearIsMeasuredKeepsTheLevelBeforeTheClear()
    {
        // Grow climbs 30 MB a minute; its incident reaches its 20-minute maximum while PoolWatch's clear (which drops Grow by
        // 300 MB) is still measured. It closes then with the level before the clear, not the clear's dip.
        const int clearAfter = 155;
        long Grow(int i) => 100 * MB + i * 5 * MB - (i > clearAfter ? 300 * MB : 0);
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 20 });
        var grow = PoolTag.Parse("Grow");
        int closedAt = -1;
        for (int i = 0; i < 170 && closedAt < 0; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            if (i == clearAfter) a.Clear();
            if (i > clearAfter && !a.Open[i].Contains(grow)) closedAt = i;
        }
        Assert.True(a.Holding[closedAt]);
        var inc = Assert.Single(a.Incidents.All, x => x.Tag == grow);
        Assert.Contains(inc.Timeline, t => t.TimeUtc == a.Last && t.Text.StartsWith("closed: open for the maximum", StringComparison.Ordinal));
        Assert.Equal((Grow(clearAfter), Grow(clearAfter)), (inc.CurrentBytes, inc.PeakBytes));
    }

    [Fact]
    public void AnIncidentWhoseSeriesStartsInsideAClearIsMeasuredFromTheClearsLevel()
    {
        // The history starts a minute after PoolWatch's clear (its dip at 400 MB, 450 MB when it is measured), then Over grows
        // to 1 GB. The incident's series starts inside the clear's interval: its growth counts from 450 MB.
        using var a = new ClearWorld(new Settings());
        a.Clears.Add(new ClearRecord { TimeUtc = ClearWorld.T0.AddSeconds(-60), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = default });
        long Over(int i) => i < 6 ? 400 * MB : 450 * MB + Math.Min(i - 6, 90) * 550 * MB / 90;
        for (int i = 0; i <= 100; i++) a.Sample(GB, ("Over", Over(i)));
        var inc = Assert.Single(a.Incidents.All, i => i.Tag == PoolTag.Parse("Over"));
        Assert.InRange(inc.Growth, 500 * MB, 550 * MB);
        Assert.True(inc.StartBytes >= 450 * MB);
    }

    // ------------------------------------------------------------------ clears close together

    [Fact]
    public void TwoClearsInTwoMinutesTheNonPagedRiseCountsFromTheLastClearsLevel()
    {
        // Clear 1 after sample 119; the button again 60 s later, after sample 125, while clear 1 is measured (its measurement
        // lies in clear 2's dip). The total then rises 80 MB in 8 minutes from the level both left: under the rule, as for
        // the same curve with no clear.
        const int c1 = 119, c2 = 125;
        int m2 = ClearWorld.MeasuredAt(c2);
        long Total(int i) => i <= c1 ? 6 * GB
            : i <= c2 ? 5 * GB + 200 * MB + (i - c1 - 1) * 10 * MB
            : i < m2 ? 5 * GB + (i - c2 - 1) * 150 * MB / (m2 - c2 - 1)
            : 5 * GB + 150 * MB + Math.Min(i - m2, 48) * 80 * MB / 48;
        long WithoutClear(int i) => i < m2 ? Total(m2) : Total(i);
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 260; i++)
        {
            a.Sample(Total(i), ("MmSt", Total(i) - 4 * GB));
            b.Sample(WithoutClear(i), ("MmSt", WithoutClear(i) - 4 * GB));
            if (i == c1 || i == c2) a.Clear();
        }
        Assert.Empty(b.Alerts.SelectMany(x => x));
        Assert.Empty(a.Alerts.SelectMany(x => x));
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        // One interval, from the sample before the first to the one that measured the second.
        Assert.Equal(new ClearSpan(c1, m2), Assert.Single(a.Detector.Clears.SpansIn(a.History).Spans));
    }

    [Fact]
    public void TwoClearsInTwoMinutesATagsChangeCountsFromTheLastClearsLevel()
    {
        // Clear 1 drops Reff from 600 to 100 MB; it refills to 180 MB; clear 2 (60 s later) dips it to 50 MB and it is back
        // at 200 MB when clear 2 is measured, then flat. The same curve with no clear: 200 MB all along.
        const int c1 = 119, c2 = 125;
        int m2 = ClearWorld.MeasuredAt(c2);
        long ReffAt(int i) => i <= c1 ? 600 * MB
            : i <= c2 ? 100 * MB + (i - c1 - 1) * 16 * MB
            : i < m2 ? 50 * MB + (i - c2 - 1) * 150 * MB / (m2 - c2 - 1)
            : 200 * MB;
        long WithoutClear(int i) => i < m2 ? 200 * MB : ReffAt(i);
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 260; i++)
        {
            a.Sample(GB, ("Reff", ReffAt(i)));
            b.Sample(GB, ("Reff", WithoutClear(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        for (int i = m2; i < 260; i++) Assert.Equal(Said(b.Status[i], Reff), Said(a.Status[i], Reff));
        Assert.All(a.Holding.Skip(c1 + 1).Take(m2 - c1 - 1), x => Assert.True(x)); // held until the last of them is measured
    }

    [Theory]
    [InlineData(4)]  // both in one sample interval
    [InlineData(60)] // the first is measured while the second still is
    public void TwoClearsCloseTogetherLowerAnIncidentsHighByTheirDropOnce(int apartSeconds)
    {
        // The plateau of AClearsDropLowersAnIncidentsHighOnceSoItClosesAsWithoutTheClear, with two clears that free 50 MB
        // each: the incident still closes ten minutes after the last real rise.
        const int flatFrom = 180, c1 = 200;
        int c2 = c1 + apartSeconds / 10;
        long Plat(int i) => 100 * MB + Math.Min(i, flatFrom) * 7 * MB;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        var plat = PoolTag.Parse("Plat");
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Plat", Plat(i) - (i > c1 ? 50 * MB : 0) - (i > c2 ? 50 * MB : 0)));
            b.Sample(GB, ("Plat", Plat(i)));
            if (i == c1) a.Clear();
            if (i == c1 && c2 == c1)
                a.Clears.Add(new ClearRecord { TimeUtc = a.Last.AddSeconds(1 + apartSeconds), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = a.History.Totals[^1] });
            else if (i == c2 && c2 != c1) a.Clear();
        }
        int ClosedAt(ClearWorld w) => w.Open.FindIndex(w.Open.FindIndex(o => o.Contains(plat)), o => !o.Contains(plat));
        Assert.Equal(flatFrom + 60, ClosedAt(b));
        Assert.Equal(ClosedAt(b), ClosedAt(a));
    }

    // ------------------------------------------------------------------ a window that holds a clear's interval

    [Theory]
    [InlineData(360, 1050, false)] // hourly clears, 2.5% over the hour rule: an hour less one clear's 13 samples is 1012 MB
    [InlineData(360, 1070, true)]  // 4.5% over it: 1031 MB, over the rule
    [InlineData(180, 1070, false)] // every 30 minutes, the size rule's spacing: two intervals in every hour, 993 MB
    [InlineData(180, 1120, true)]  // 9.4% over it: 1039 MB
    public void ALeakJustOverTheHourRuleThatClearsKeepTrimmingIsFoundOnlyOverTheRuleLessTheClearsMinutes(int every, int mbPerHour, bool found)
    {
        // Trim grows mbPerHour (the 10-minute rule cannot see it) and each clear frees 100 MB of it. Its growth in a clear's
        // 2 minutes is the clear's, and the rule keeps its own 1 GB: an hour that holds one clear's interval holds 347 of its
        // 360 samples of growth, one that holds two 334. Once every hour holds that many (from the second clear's measurement
        // on), a leak under 1 GB over those samples goes unreported and its incident closes, at this (the default) 10-second
        // sampling about 4% over the rule with hourly clears and about 8% with clears every 30 minutes; one over it is found
        // at every sample but the clears', and its incident stays open with no second notice. The rule's words and bar are
        // its own throughout.
        var settings = new Settings { IncidentMaxMinutes = 600 };
        int[] clearsAfter = Enumerable.Range(0, 7).Select(k => 359 + k * every).Where(k => k < 1300).ToArray();
        long Trim(int i) => GB + i * mbPerHour * MB / 360 - clearsAfter.Count(k => i > k) * 100 * MB;
        using var a = new ClearWorld(settings);
        for (int i = 0; i < 1300; i++)
        {
            a.Sample(GB, ("Trim", Trim(i)));
            if (clearsAfter.Contains(i)) a.Clear();
        }
        var trim = PoolTag.Parse("Trim");
        bool Hour(int i) => a.Alerts[i].Any(x => x.Tag == trim && x.Window == TimeSpan.FromHours(1));
        int second = ClearWorld.MeasuredAt(clearsAfter[1]);
        for (int i = second; i < 1300; i++)
            if (!a.Holding[i]) Assert.Equal(found, Hour(i));
        AssertRulesUnchanged(a);
        int opened = a.Open.FindIndex(o => o.Contains(trim));
        Assert.InRange(opened, 0, clearsAfter[0]); // the hour before the first clear holds no clear
        Assert.Equal(found, a.Open[^1].Contains(trim));
        if (!found) return;
        Assert.All(a.Open.Skip(opened), o => Assert.Contains(trim, o));
        Assert.Equal(0, Assert.Single(a.Incidents.All, x => x.Tag == trim).Reopened);
        long held = (360 - (every == 360 ? 13 : 26)) * (long)mbPerHour * MB / 360;
        Assert.InRange(a.Alerts[second + 6].Single(x => x.Window == TimeSpan.FromHours(1)).Bytes, held - 4096, held + 4096); // to the KB the history holds
    }

    [Theory]
    [InlineData(10, true)]  // the default sampling: an hour holding one clear holds 347 of its 360 samples of growth, 1031 MB
    [InlineData(60, false)] // a sample a minute: the clear's interval is 3 of the hour's 60 samples, so it holds 1016 MB
    public void TheBandALeakCanHideInGrowsWithTheSamplingInterval(int seconds, bool found)
    {
        // The limit's sizes are said for the default 10-second sampling. Trim grows 1070 MB an hour (4.5% over the hour rule)
        // and each hourly clear frees 100 MB of it. A clear's interval is its 2 minutes rounded up to the sample grid, so at a
        // sample a minute it takes 3 minutes of every hour: the same leak goes unreported from the second clear's measurement
        // on. The same leak with no clear is found at every one of those samples, at either sampling.
        var step = TimeSpan.FromSeconds(seconds);
        int perHour = 3600 / seconds, samples = 4 * perHour;
        int[] clearsAfter = [perHour - 1, 2 * perHour - 1, 3 * perHour - 1];
        long Trim(int i, bool cleared) => GB + i * 1070L * MB / perHour - (cleared ? clearsAfter.Count(k => i > k) * 100 * MB : 0);
        var settings = new Settings { IncidentMaxMinutes = 600 };
        using var a = new ClearWorld(settings, step: step);
        using var none = new ClearWorld(settings, step: step);
        for (int i = 0; i < samples; i++)
        {
            a.Sample(GB, ("Trim", Trim(i, true)));
            none.Sample(GB, ("Trim", Trim(i, false)));
            if (clearsAfter.Contains(i)) a.Clear();
        }
        var trim = PoolTag.Parse("Trim");
        bool Hour(ClearWorld w, int i) => w.Alerts[i].Any(x => x.Tag == trim && x.Window == TimeSpan.FromHours(1));
        int second = Enumerable.Range(0, samples).First(i => ClearWorld.T0 + step * i >= a.Clears[1].TimeUtc + ClearWorld.Settle), checkedSamples = 0;
        Assert.Equal(second, a.MeasuredAfter(clearsAfter[1]));
        Assert.Equal(seconds == 10 ? 13 : 3, second - clearsAfter[1]); // the clear's interval, in samples
        for (int i = second; i < samples; i++)
        {
            Assert.True(Hour(none, i));
            if (a.Holding[i]) continue;
            Assert.Equal(found, Hour(a, i));
            checkedSamples++;
        }
        Assert.True(checkedSamples >= perHour); // the hours after the second clear, but the clears' own minutes
    }

    [Fact]
    public void AFallBeforeAClearAndOneAfterItAreNeverAddedUpIntoARelease()
    {
        // Reff holds 1000 MB and falls 60 MB on its own; 4 minutes later PoolWatch's clear frees 200 MB of it while it grows
        // 50 MB inside the clear's 2 minutes (that growth is the clear's: the stitch adds back a 150 MB drop); after the
        // clear's measurement it falls 50 MB more. Each fall is under the 100 MB release rule, and the same curve with no
        // clear (its growth kept) never falls 100 MB inside 10 minutes. On the stitch the two falls would read as one of
        // 110 MB: a release is raised only on a fall since the clear's measurement, so there is none, the tag is never read
        // as released, and no release is recorded.
        const int clearAfter = 130;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Own(int i) => 1000 * MB - Math.Clamp(i - 99, 0, 6) * 10 * MB
                           + (i > clearAfter ? Math.Min(i - clearAfter, measured - clearAfter) * 50 * MB / (measured - clearAfter) : 0)
                           - Math.Clamp(i - 149, 0, 5) * 10 * MB;
        using var a = new ClearWorld(new Settings());
        using var none = new ClearWorld(new Settings());
        for (int i = 0; i < 260; i++)
        {
            a.Sample(GB, ("Reff", Own(i) - (i > clearAfter ? 200 * MB : 0)));
            none.Sample(GB, ("Reff", Own(i)));
            if (i == clearAfter) a.Clear();
        }
        Assert.Equal(measured, a.History.IndexAtOrAfter(a.Clears[0].TimeUtc + ClearWorld.Settle));
        Assert.DoesNotContain(none.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        Assert.All(a.Status, s => Assert.NotEqual(TagState.Released, s.TryGetValue(Reff, out var st) ? st.State : TagState.Normal));
        Assert.DoesNotContain(a.Incidents.All, x => x.Release is not null);
        // The stitch itself still reads the two falls together (the window's maximum before the clear, the fall after it):
        // 110 MB, which the rule no longer takes for a release.
        Assert.Equal(-110 * MB, a.Status[156][Reff].Delta10m);
    }

    [Fact]
    public void AReleaseFoundBeforeAClearStaysReleasedAcrossItButIsNotRaisedAgainThere()
    {
        // Reff falls 150 MB (a release, raised), is back 10 samples later, and falls 150 MB again 8 minutes after the first:
        // released again, its alert held back by the release rule's 10 minutes. A clear then frees 20 MB of it. Once the
        // clear is measured the tag still reads as released (its window still holds the fall), but no release is raised
        // there: nothing fell since the clear's measurement.
        const int clearAfter = 105;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long ReffAt(int i) => (i is >= 50 and < 60 || i >= 100 ? 850 * MB : 1000 * MB) - (i > clearAfter ? 20 * MB : 0);
        using var a = new ClearWorld(new Settings());
        for (int i = 0; i <= measured + 10; i++)
        {
            a.Sample(GB, ("Reff", ReffAt(i)));
            if (i == clearAfter) a.Clear();
        }
        var releases = a.Alerts.Select((x, i) => (i, x)).Where(p => p.x.Any(y => y.Kind == AlertKind.Release)).Select(p => p.i).ToList();
        Assert.Equal([50], releases);
        Assert.Equal(TagState.Released, a.Status[100][Reff].State);
        Assert.Equal(TagState.Released, a.Status[measured][Reff].State);
        Assert.Equal(TagState.Released, a.Status[measured + 10][Reff].State);
    }

    [Fact]
    public void ATagJustOverTheGrowingRateReadsNormalWhileItsTenMinutesHoldAClearThatTrimsIt()
    {
        // Slow grows 1.1 MB a minute (11 MB in 10 minutes: the growing state starts at 10 MB) and a clear trims 5 MB of it.
        // Its growth in the clear's 2 minutes is the clear's, and the state keeps its own 10 MB: while its 10 minutes hold
        // the clear's interval they hold 7.8 minutes of growth (8.6 MB), so it reads normal there and growing at every other
        // sample, as the same tag holding still through the clear's 2 minutes does with no clear ("paused").
        const int clearAfter = 200;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Grow(int i) => 300 * MB + i * 11 * MB / 60;
        long Paused(int i) => i <= clearAfter ? Grow(i) : i < measured ? Grow(clearAfter) : Grow(i) - (Grow(measured) - Grow(clearAfter));
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Slow", Grow(i) - (i > clearAfter ? 5 * MB : 0)));
            b.Sample(GB, ("Slow", Paused(i)));
            if (i == clearAfter) a.Clear();
        }
        var slow = PoolTag.Parse("Slow");
        for (int i = 60; i < 330; i++)
            if (!a.Holding[i]) Assert.Equal(b.Status[i][slow].State, a.Status[i][slow].State);
        Assert.Equal(TagState.Growing, a.Status[clearAfter][slow].State);
        Assert.Equal(TagState.Normal, a.Status[measured + 6][slow].State);
        Assert.True(a.Status[measured + 6][slow].Delta10m < 10 * MB);
        Assert.Equal(TagState.Growing, a.Status[measured + 60][slow].State);
    }

    [Theory]
    [InlineData(1.0)] // a second after a sample: measured 126 s (18 samples) after the sample before it
    [InlineData(6.5)] // late in the sample period: 133 s (19 samples)
    public void AtSevenSecondSamplesATrimmedTagReadsAsTheSameTagHoldingStillThroughTheClear(double offsetSeconds)
    {
        // At a sample every 7 seconds, a 10-minute window holds 85 intervals (9.9 minutes), and the growing state scales the
        // change in it to 10 minutes. Slow grows 120 KB a sample: 9.96 MB in the window, 10.04 MB scaled, just growing. A clear
        // frees 300 MB of it, wherever in a sample period it runs: its interval is what the samples make it, and Slow reads
        // at every sample as the same tag holding still for that interval with no clear (world p), normal while its 10
        // minutes hold the interval and growing again after.
        var step = TimeSpan.FromSeconds(7);
        const int clearAfter = 150, total = 300;
        int cut = (int)Math.Ceiling((offsetSeconds + ClearWorld.Settle.TotalSeconds) / step.TotalSeconds);
        var slow = PoolTag.Parse("Slow");
        long Own(int k) => 500 * MB + k * 120 * 1024L;
        long A(int i) => i <= clearAfter ? Own(i) : i < clearAfter + cut ? Own(clearAfter) - 350 * MB : Own(i - cut) - 300 * MB;
        long P(int i) => i <= clearAfter ? Own(i) : i < clearAfter + cut ? Own(clearAfter) : Own(i - cut);
        Sample At(int i, long v) => new(ClearWorld.T0 + step * i, new PoolTotals(1UL << 30, 1UL << 30, 0, 0), [new PoolTagEntry(slow, 0, 0, 0, 1, 0, (ulong)v)], []);
        var (ha, hp) = (History.ForInterval(7), History.ForInterval(7));
        var (da, dp) = (new Detector(new Settings()), new Detector(new Settings()));
        var clears = new List<ClearRecord>();
        var (sa, sp) = (new List<TagState?>(), new List<TagState?>());
        for (int i = 0; i < total; i++)
        {
            ha.Add(At(i, A(i)));
            da.Evaluate(ha, new OwnClears(clears, ClearWorld.Settle));
            sa.Add(da.Holding ? null : da.Status.GetValueOrDefault(slow)?.State);
            if (i == clearAfter) clears.Add(new ClearRecord { TimeUtc = ClearWorld.T0 + step * i + TimeSpan.FromSeconds(offsetSeconds), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = default });
            hp.Add(At(i, P(i)));
            dp.Evaluate(hp);
            sp.Add(dp.Status.GetValueOrDefault(slow)?.State);
        }
        Assert.Equal(clearAfter + cut, ha.IndexAtOrAfter(clears[0].TimeUtc + ClearWorld.Settle));
        for (int i = 90; i < total; i++)
            if (sa[i] is { } state) Assert.Equal(sp[i], state);
        Assert.Equal(TagState.Growing, sa[clearAfter]);
        Assert.Equal(TagState.Normal, sa[clearAfter + cut + 6]);   // its 10 minutes hold the clear's interval
        Assert.Equal(TagState.Growing, sa[clearAfter + cut + 90]); // they no longer do
    }

    [Fact]
    public void ARisingNonPagedTotalJustOverItsRuleLapsesWhileItsTenMinutesHoldAClearThatTrimsIt()
    {
        // The non-paged total rises 11 MB a minute from 6.5 GB (the rule: over 5 GB and 100 MB in 10 minutes), carried by
        // NpLk, and each hourly clear frees 300 MB of it. Its rise in a clear's 2 minutes is the clear's, and the rule keeps
        // its own 100 MB: 10 minutes that hold a clear's interval hold 47 samples of the rise (86 MB), and 10 minutes that
        // start inside it are measured from its measurement. So the rule lapses until 55 samples (101 MB) have passed since
        // the measurement, and fires at every other sample, in its plain words.
        var settings = new Settings { IncidentMaxMinutes = 600 };
        int[] clearsAfter = [359, 719];
        long NpLk(int i) => GB + i * 11 * MB / 6 - clearsAfter.Count(k => i > k) * 300 * MB;
        using var a = new ClearWorld(settings);
        for (int i = 0; i < 1000; i++)
        {
            a.Sample(4 * GB + 512 * MB + NpLk(i), ("NpLk", NpLk(i)));
            if (clearsAfter.Contains(i)) a.Clear();
        }
        bool Fires(int i) => a.Alerts[i].Any(x => x.Kind == AlertKind.NonPagedTotal && x.Tag == PoolTag.Parse("NpLk"));
        int opened = a.Alerts.FindIndex(x => x.Any(y => y.Kind == AlertKind.NonPagedTotal));
        Assert.InRange(opened, 0, clearsAfter[0]);
        int m = ClearWorld.MeasuredAt(clearsAfter[0]);
        for (int i = opened; i < clearsAfter[1]; i++)
            if (!a.Holding[i]) Assert.Equal(i < m || i - m >= 55, Fires(i));
        Assert.EndsWith(" and rising (+110 MB in 10 min)", a.Alerts[clearsAfter[0] - 1].Single(x => x.Kind == AlertKind.NonPagedTotal).Rule);
        var after = a.Alerts[m + 60].Single(x => x.Kind == AlertKind.NonPagedTotal);
        Assert.EndsWith(" and rising (+110 MB in 10 min)", after.Rule);
        Assert.Equal((110 * MB, ClearWorld.T0 + ClearWorld.Interval * m), (after.Bytes, after.OnsetUtc));
        AssertRulesUnchanged(a);
    }

    [Fact]
    public void AnIncidentAClearDelaysKeepsItsLevelThenAsItsHigh()
    {
        // Clmb starts growing 25 MB a minute 7 minutes before a clear that frees 300 MB of it. Where no clear ran it crosses
        // the 10-minute rule inside what is the clear's interval here; that interval's growth is the clear's, and the rule
        // keeps its own 200 MB, so here its 10 minutes hold 47 samples of the climb (196 MB) until they start inside the
        // interval and are measured from its measurement: the incident opens later, never sooner. Its high-water mark is
        // its level then (the clear's drop came before the incident and is not taken off it).
        const int clearAfter = 200, from = clearAfter - 42;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Climb(int i) => 100 * MB + Math.Max(0, i - from) * 25 * MB / 6;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        var climb = PoolTag.Parse("Clmb");
        (long High, long Level)? atOpen = null;
        for (int i = 0; i < measured + 90; i++)
        {
            a.Sample(GB, ("Clmb", Climb(i) - (i > clearAfter ? 300 * MB : 0)));
            b.Sample(GB, ("Clmb", Climb(i)));
            if (i == clearAfter) a.Clear();
            if (atOpen is null && a.Incidents.Open.FirstOrDefault(x => x.Tag == climb) is { } inc) atOpen = (inc.HighestSeenBytes, Level(a, climb));
        }
        int opened = b.Open.FindIndex(o => o.Contains(climb));
        Assert.InRange(opened, clearAfter + 1, measured - 1);
        int openedA = a.Open.FindIndex(o => o.Contains(climb));
        Assert.InRange(openedA, clearAfter + 61, measured + 59); // once its 10 minutes start inside the clear's interval
        Assert.Equal(atOpen!.Value.Level, atOpen.Value.High);
        Assert.All(a.Open.Skip(openedA), o => Assert.Contains(climb, o));
        Assert.All(a.Alerts.Take(openedA).SelectMany(x => x), x => Assert.NotEqual(climb, x.Tag));
    }

    [Fact]
    public void ANonPagedIncidentOpenedAtAClearsMeasurementKeepsItsLevelThenAsItsHigh()
    {
        // The non-paged total (carried by NpLk) falls 50 MB on its own, then climbs 140 MB in 9 minutes up to PoolWatch's
        // clear, which frees 300 MB of it. With no clear its rule fires inside what is the clear's interval here, once its
        // 10 minutes no longer hold the fall; here nothing is raised while the clear is measured, so the system incident opens
        // at the measurement. Its high-water mark is the total's level then: the clear's drop came before the incident and
        // is not taken off it.
        const int clearAfter = 200, fall = clearAfter - 55;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Total(int i) => 6 * GB - (i >= fall ? 50 * MB : 0) + Math.Clamp(i - fall, 0, 55) * 140 * MB / 55;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        long? highAtOpen = null, levelAtOpen = null;
        for (int i = 0; i < measured + 30; i++)
        {
            long t = Total(i) - (i > clearAfter ? 300 * MB : 0);
            a.Sample(t, ("NpLk", t - 4 * GB - 512 * MB));
            b.Sample(Total(i), ("NpLk", Total(i) - 4 * GB - 512 * MB));
            if (i == clearAfter) a.Clear();
            if (highAtOpen is null && a.Incidents.Open.FirstOrDefault(x => x.Kind == IncidentKind.NonPagedTotal) is { } inc)
                (highAtOpen, levelAtOpen) = (inc.HighestSeenBytes, (long)a.History.Totals[^1].CommittedNonPaged);
        }
        bool Opened(ClearWorld w, int i) => w.Open[i].Count > 0;
        Assert.InRange(Enumerable.Range(0, measured + 30).First(i => Opened(b, i)), clearAfter + 1, measured - 1);
        Assert.Equal(measured, Enumerable.Range(0, measured + 30).First(i => Opened(a, i)));
        Assert.Equal(levelAtOpen, highAtOpen);
        Assert.Equal(Total(clearAfter) - 300 * MB, highAtOpen);
    }

    [Theory]
    [InlineData(6, true)]   // it rose a minute before the clear: the clear's interval hid whether it went on
    [InlineData(18, false)] // three minutes before: it had stopped; it closes ten minutes after its last rise
    public void AnIncidentRisingWhenAClearDropsItCountsItsQuietMinutesFromTheMeasurement(int stoppedBefore, bool rising)
    {
        // Grow climbs 30 MB a minute and stops shortly before PoolWatch clears the cache, which frees 300 MB of it. Still
        // rising when the clear ran (within the 2 minutes between a rule's runs), its incident counts its ten quiet minutes
        // from the clear's measurement; its last rise stays the last one seen (its report's time spans read it).
        const int clearAfter = 200;
        int stopAt = clearAfter - stoppedBefore, measured = ClearWorld.MeasuredAt(clearAfter);
        long Grow(int i) => 100 * MB + Math.Min(i, stopAt) * 5 * MB - (i > clearAfter ? 300 * MB : 0);
        using var a = new ClearWorld(new Settings());
        var grow = PoolTag.Parse("Grow");
        for (int i = 0; i < measured + 90; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            if (i == clearAfter) a.Clear();
        }
        int opened = a.Open.FindIndex(o => o.Contains(grow));
        Assert.Equal(rising ? measured + 60 : stopAt + 60, a.Open.FindIndex(opened, o => !o.Contains(grow)));
        var inc = Assert.Single(a.Incidents.All, x => x.Tag == grow);
        Assert.Equal(a.History.Times[stopAt], inc.LastRiseUtc);
    }

    // ------------------------------------------------------------------ what the reports say of the clears

    [Fact]
    public void AnIncidentTwoClearsFreedPartOfSaysTheClearsFreedItWithNoOneTime()
    {
        // Toke leaks 40 MB a minute and two clears 30 minutes apart each free part of it: its report says the clears freed
        // their sum, with no one time.
        const int c1 = 179, c2 = 359;
        int at = ClearWorld.MeasuredAt(c2) + 30;
        long Toke(int i) => GB + i * 40 * MB / 6 - (i > c1 ? 200 * MB : 0) - (i > c2 ? 200 * MB : 0);
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        for (int i = 0; i <= at; i++)
        {
            a.Sample(GB, ("Toke", Toke(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        var toke = PoolTag.Parse("Toke");
        long V(int i) { var (p, np) = a.History.TagAt(toke, i); return p + np; }
        var inc = Assert.Single(a.Incidents.Open, x => x.Tag == toke);
        a.Incidents.Rank(a.History, inc, a.Last);
        long freed = V(c1) - V(ClearWorld.MeasuredAt(c1)) + V(c2) - V(ClearWorld.MeasuredAt(c2));
        Assert.Equal(freed, inc.ClearedBytes);
        Assert.Null(inc.ClearedAtUtc);
        Assert.Equal($"PoolWatch's own cache clears freed {Fmt.Bytes(freed)}", inc.OwnClearsFreed);
    }

    [Fact]
    public void AReleaseRecordNamesOnlyTheClearThatFreedPartOfIt()
    {
        // Reff holds 600 MB; clear 1 drops it to 250 MB, clear 2 three minutes later frees none of it, then Reff falls 200 MB
        // on its own: the one clear that freed part of it is said, with its time.
        const int c1 = 120, c2 = 140;
        int m2 = ClearWorld.MeasuredAt(c2);
        long ReffAt(int i) => i <= c1 ? 600 * MB : i < m2 + 6 ? 250 * MB : 50 * MB;
        using var a = new ClearWorld(new Settings());
        for (int i = 0; i < m2 + 30; i++)
        {
            a.Sample(GB, ("Reff", ReffAt(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        Assert.Equal(200 * MB, Assert.Single(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release).Bytes);
        var release = Assert.Single(a.Incidents.All, x => x.Tag == Reff).Release!;
        Assert.Equal((600 * MB, 50 * MB, 350 * MB, 200 * MB), (release.FromBytes, release.ToBytes, release.ClearedBytes, release.Dropped));
        Assert.Equal(a.Clears[0].TimeUtc, release.ClearedAtUtc);
    }

    [Fact]
    public void AReleaseTwoClearsFreedPartOfSaysTheClearsFreedItWithNoOneTime()
    {
        // Reff holds 600 MB; clear 1 drops it to 400 MB, clear 2 three minutes later to 250 MB, then Reff falls 200 MB on its
        // own: the two clears freed 350 MB of it between them, said as the clears', with no one time.
        const int c1 = 120, c2 = 140;
        int m2 = ClearWorld.MeasuredAt(c2);
        long ReffAt(int i) => i <= c1 ? 600 * MB : i <= c2 ? 400 * MB : i < m2 + 6 ? 250 * MB : 50 * MB;
        using var a = new ClearWorld(new Settings());
        for (int i = 0; i < m2 + 30; i++)
        {
            a.Sample(GB, ("Reff", ReffAt(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        var release = Assert.Single(a.Incidents.All, x => x.Tag == Reff).Release!;
        Assert.Equal((600 * MB, 50 * MB, 350 * MB, 200 * MB), (release.FromBytes, release.ToBytes, release.ClearedBytes, release.Dropped));
        Assert.Null(release.ClearedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)] // a page lower from the measurement on, as a series moves between two samples: not dropped by the clear
    public void AClearThatDropsOneTagJudgesNoOtherTagOverFewerMinutes(int lowerKb)
    {
        // Reff is dropped 300 MB by a clear; Brst grew 180 MB in a minute five minutes before it and the clear frees none of
        // it. Reff's clear changes nothing of Brst's windows: 180 MB is under the 10-minute rule's 200 MB, as with no clear,
        // at every sample.
        const int clearAfter = 200, burst = clearAfter - 30;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Burst(int i) => 100 * MB + Math.Clamp(i - burst, 0, 6) * 30 * MB - (i >= measured ? lowerKb * 1024L : 0);
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Reff", i > clearAfter ? 300 * MB : 600 * MB), ("Brst", Burst(i)));
            b.Sample(GB, ("Reff", 600 * MB), ("Brst", Burst(i)));
            if (i == clearAfter) a.Clear();
        }
        var brst = PoolTag.Parse("Brst");
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Tag == brst);
        for (int i = 0; i < 330; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(b.Alerts[i].Where(x => x.Tag == brst), a.Alerts[i].Where(x => x.Tag == brst));
                Assert.Equal(StateOf(b, i, brst), StateOf(a, i, brst));
            }
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        Assert.Equal(PoolTag.Parse("Reff"), a.History.KnownTags.First()); // read before Brst
    }

    [Fact]
    public void AReleaseFromTheSampleBeforeAClearLeavesTheClearsDropOut()
    {
        // Slow grows to the clear (so the release window's maximum is the last sample before it); the clear drops it 300 MB;
        // after the measurement it falls 200 MB on its own. The release is 200 MB and its record says so: from the real
        // level then, with the clear's 300 MB said as the clear's.
        const int clearAfter = 200;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long top = 600 * MB + clearAfter * MB / 8;
        long SlowAt(int i) => i <= clearAfter ? 600 * MB + i * MB / 8 : i < measured + 18 ? top - 300 * MB : top - 500 * MB;
        using var a = new ClearWorld(new Settings());
        var slow = PoolTag.Parse("Slow");
        for (int i = 0; i < 260; i++)
        {
            a.Sample(GB, ("Slow", SlowAt(i)));
            if (i == clearAfter) a.Clear();
        }
        var alert = Assert.Single(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        Assert.Equal((a.History.Times[clearAfter], 200 * MB), (alert.OnsetUtc, alert.Bytes));
        var rec = Assert.Single(a.Incidents.All, x => x.Tag == slow && x.Release is not null).Release!;
        Assert.Equal((top, top - 500 * MB, 300 * MB, 200 * MB), (rec.FromBytes, rec.ToBytes, rec.ClearedBytes, rec.Dropped));
    }

    [Fact]
    public void AClosedIncidentRankedAgainOnceTheClearItClosedInIsMeasuredKeepsItsLevels()
    {
        // AnIncidentClosedAtItsMaximumWhileAClearIsMeasuredKeepsTheLevelBeforeTheClear, then ranked again once the clear is
        // measured (as a confirmation trace attaching does, at its close): its window ends inside the clear's interval.
        const int clearAfter = 155;
        long Grow(int i) => 100 * MB + Math.Min(i, clearAfter) * 5 * MB - (i > clearAfter ? 300 * MB : 0);
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 20 });
        var grow = PoolTag.Parse("Grow");
        int closedAt = -1;
        for (int i = 0; i < 200; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            if (i == clearAfter) a.Clear();
            if (closedAt < 0 && i > clearAfter && !a.Open[i].Contains(grow)) closedAt = i;
        }
        Assert.InRange(closedAt, clearAfter + 1, ClearWorld.MeasuredAt(clearAfter) - 1);
        var inc = Assert.Single(a.Incidents.All, x => x.Tag == grow);
        Assert.Equal(a.History.Times[closedAt], inc.ClosedUtc);
        a.Incidents.Rank(a.History, inc, inc.ClosedUtc!.Value);
        Assert.Equal((Grow(clearAfter), Grow(clearAfter), 0L), (inc.CurrentBytes, inc.PeakBytes, inc.ClearedBytes));
    }

    // ------------------------------------------------------------------ a clear neither lowers a rule's bar nor raises a finding

    /// <summary>A tag's state after a sample (none before the history holds two).</summary>
    private static TagState? StateOf(ClearWorld w, int i, PoolTag tag) => w.Status[i].TryGetValue(tag, out var s) ? s.State : null;

    /// <summary>
    /// Every alert asks the rule's own bytes over the rule's own minutes, in the rule's plain words, whatever clear is in its
    /// window: a growth rule's "grew > 200 MB in 10 min" with more than 200 MB found, the non-paged rule's rise of 100 MB or more.
    /// </summary>
    private static void AssertRulesUnchanged(ClearWorld w)
    {
        foreach (var x in w.Alerts.SelectMany(x => x).Where(x => x.Kind == AlertKind.Growth))
        {
            var (rule, bytes) = x.Window == TimeSpan.FromMinutes(10) ? ("grew > 200 MB in 10 min", 200 * MB) : ("grew > 1.0 GB in 60 min", 1024 * MB);
            Assert.Equal(rule, x.Rule);
            Assert.True(x.Bytes > bytes, $"{x.Rule}: {x.Bytes / MB} MB");
        }
        foreach (var x in w.Alerts.SelectMany(x => x).Where(x => x.Kind == AlertKind.NonPagedTotal))
        {
            var m = System.Text.RegularExpressions.Regex.Match(x.Rule, @"and rising \(\+([0-9.]+) (MB|GB) in 10 min\)$");
            Assert.True(m.Success, x.Rule);
            Assert.True(m.Groups[2].Value == "GB" || double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 100, x.Rule);
        }
    }

    [Theory]
    [InlineData("burst")]  // 190 MB in a minute, 3 minutes before the clear: under the 10-minute rule
    [InlineData("refill")] // flat, then 190 MB in the 6 minutes after the clear's measurement: under it too
    [InlineData("leak")]   // 21 MB a minute through the clear: over it, and over the hour rule
    [InlineData("hour")]   // 17.3 MB a minute: only the hour rule, just over its 1 GB
    [InlineData("slow")]   // 1.05 MB a minute: just over the growing state's 10 MB in 10 minutes
    public void EveryRuleFindsWhatItFindsOnTheSameCurveHoldingStillThroughTheClearAndNeverMore(string shape)
    {
        // Sssn's own level at each of its own samples is a curve with no clear (world n). PoolWatch's clear after sample 400
        // frees 300 MB of it, and the cache moves it around inside the interval. Its growth in the interval's 13 samples is
        // the clear's:
        // - in world a, Sssn holds still for the interval and from the measurement on moves as its own curve does, 300 MB
        //   lower. At every sample a finds what the same curve holding still through the interval finds with no clear
        //   (world p): the same rules, bytes, onsets and states, by the clock, in each rule's own words and with its own bar.
        // - in world g, Sssn goes on growing through the interval as its own curve does, 300 MB lower from the measurement
        //   on. g never finds more than that curve with no clear (world n) at the same sample: every rule g raises, n raises
        //   with at least as many bytes, and g is growing only where n is.
        // The non-paged total carries it, so its rule reads the same.
        const int clearAfter = 400, total = 860;
        int measured = ClearWorld.MeasuredAt(clearAfter), cut = measured - clearAfter;
        Func<int, long> own = shape switch
        {
            "burst" => k => Math.Clamp(k - (clearAfter - 24), 0, 6) * 190 * MB / 6,
            "refill" => k => Math.Clamp(k - clearAfter, 0, 36) * 190 * MB / 36,
            "leak" => k => k * 21 * MB / 6,
            "hour" => k => k * 173 * MB / 60,
            _ => k => k * 105 * MB / 600,
        };
        long Own(int k) => GB + own(k);
        long Dip(int i) => Own(clearAfter) - 300 * MB - (i - clearAfter) * 20 * MB;
        long A(int i) => i <= clearAfter ? Own(i) : i < measured ? Dip(i) : Own(i - cut) - 300 * MB;
        long P(int i) => i <= clearAfter ? Own(i) : i < measured ? Own(clearAfter) : Own(i - cut);
        long G(int i) => i <= clearAfter ? Own(i) : i < measured ? Dip(i) : Own(i) - 300 * MB;
        var worlds = Enumerable.Range(0, 4).Select(_ => new ClearWorld(new Settings { IncidentMaxMinutes = 600 })).ToArray();
        var (a, p, g, n) = (worlds[0], worlds[1], worlds[2], worlds[3]);
        try
        {
            for (int i = 0; i < total; i++)
            {
                foreach (var (w, v) in new[] { (a, A(i)), (p, P(i)), (g, G(i)), (n, Own(i)) }) w.Sample(4 * GB + 512 * MB + v, ("Sssn", v));
                if (i == clearAfter) { a.Clear(); g.Clear(); }
            }
            var tag = PoolTag.Parse("Sssn");
            for (int i = 0; i < total; i++)
            {
                if (a.Holding[i]) continue;
                Assert.Equal(Found(p.Alerts[i]), Found(a.Alerts[i]));
                Assert.Equal(StateOf(p, i, tag), StateOf(a, i, tag));
                foreach (var x in g.Alerts[i])
                    Assert.Contains(n.Alerts[i], y => y.Kind == x.Kind && y.Window == x.Window && y.Bytes >= x.Bytes);
                if (StateOf(g, i, tag) == TagState.Growing) Assert.Equal(TagState.Growing, StateOf(n, i, tag));
            }
            Assert.Equal(p.Incidents.CreatedCount, a.Incidents.CreatedCount);
            AssertRulesUnchanged(a);
            AssertRulesUnchanged(g);
            var found = a.Alerts.SelectMany(x => x).ToList();
            Assert.Equal(shape is "leak" or "hour", found.Any(x => x.Kind == AlertKind.Growth && x.Window == TimeSpan.FromHours(1)));
            Assert.Equal(shape == "leak", found.Any(x => x.Kind == AlertKind.Growth && x.Window == TimeSpan.FromMinutes(10)));
            // The limit at an edge: while a window holds the clear's interval, it holds that many fewer samples of growth.
            if (shape == "slow") Assert.Equal((TagState.Normal, TagState.Growing), (StateOf(g, measured + 6, tag), StateOf(n, measured + 6, tag)));
            if (shape == "hour")
                Assert.Equal((false, true), (g.Alerts[measured + 6].Any(x => x.Window == TimeSpan.FromHours(1)), n.Alerts[measured + 6].Any(x => x.Window == TimeSpan.FromHours(1))));
        }
        finally { foreach (var w in worlds) w.Dispose(); }
    }

    [Theory]
    [InlineData(600)]  // the hour rule needs over 1 GB, and no hour holds two whole jobs: nothing from it, with the clears or without
    [InlineData(900)]
    [InlineData(1100)] // over it: the hour rule fires for each job, with the clears as without them
    public void AnHourlyJobOnATagTheHourlyClearFreesRaisesExactlyWhatItRaisesWithNoClears(int jobMb)
    {
        // Reff gains jobMb in a minute at 10 past every hour (an hourly job, such as File History or a task set to repeat
        // hourly) and holds it; PoolWatch's hourly clear at 40 past frees it. The stitch adds each clear's drop back, so with
        // the clears Reff reads as the same staircase with no clear (world b): every rule raises what it raises there, at the
        // same samples, from the same onsets, and as many incidents open. A clear's 2 minutes never join the jobs on both
        // sides of it into one hour.
        int[] jobs = [.. Enumerable.Range(0, 5).Select(k => 60 + k * 360)], clearsAfter = [.. Enumerable.Range(0, 5).Select(k => 240 + k * 360)];
        long Done(int i) => jobs.Sum(j => (long)Math.Clamp(i - j, 0, 6) * jobMb * MB / 6);
        long Staircase(int i) => 200 * MB + Done(i);
        long WithClears(int i) => Staircase(i) - clearsAfter.Count(k => i > k) * (long)jobMb * MB;
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        using var b = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        for (int i = 0; i < 1800; i++)
        {
            a.Sample(GB, ("Reff", WithClears(i)));
            b.Sample(GB, ("Reff", Staircase(i)));
            if (clearsAfter.Contains(i)) a.Clear();
        }
        for (int i = 0; i < 1800; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(b.Alerts[i], a.Alerts[i]);
                Assert.Equal(Said(b.Status[i], Reff), Said(a.Status[i], Reff));
            }
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        bool Hour(ClearWorld w) => w.Alerts.SelectMany(x => x).Any(x => x.Window == TimeSpan.FromHours(1));
        Assert.Equal((jobMb > 1024, jobMb > 1024), (Hour(b), Hour(a)));
        Assert.DoesNotContain(a.Alerts.SelectMany(x => x), x => x.Kind == AlertKind.Release);
        AssertRulesUnchanged(a);
    }

    [Fact]
    public void GrowthOnBothSidesOfAClearRaisesNothingWhereEachWindowWithNoClearHoldsTooLittle()
    {
        // Brst grows 120 MB in a minute; 5 minutes later PoolWatch's clear frees 300 MB of it, and 11 minutes after the first
        // burst it grows 100 MB more. With no clear no 10 minutes hold more than 120 MB of it; with the clear, each clock
        // window finds what it finds there: nothing is raised.
        const int burst = 100, clearAfter = burst + 30, second = burst + 66;
        long Brst(int i) => GB + Math.Clamp(i - burst, 0, 6) * 20 * MB + Math.Clamp(i - second, 0, 6) * 100 * MB / 6;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 300; i++)
        {
            a.Sample(GB, ("Brst", Brst(i) - (i > clearAfter ? 300 * MB : 0)));
            b.Sample(GB, ("Brst", Brst(i)));
            if (i == clearAfter) a.Clear();
        }
        var brst = PoolTag.Parse("Brst");
        Assert.Empty(b.Alerts.SelectMany(x => x));
        Assert.Empty(a.Alerts.SelectMany(x => x));
        for (int i = 0; i < 300; i++)
            if (!a.Holding[i]) Assert.Equal(StateOf(b, i, brst), StateOf(a, i, brst));
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
    }

    [Theory]
    [InlineData(0)]   // no clear in its windows at all
    [InlineData(40)]  // its burst 40 samples after the clear's measurement: windows that start inside the clear's interval
    public void ATagFlatAcrossAClearItDidNotDropKeepsTheClocksWindows(int burstAfter)
    {
        // Reff is dropped 300 MB by a clear; Flat holds 500 MB exactly across it and later grows 170 MB in a minute: under
        // the 10-minute rule. Its windows are the clock's: where one starts inside the clear's interval, it is measured from
        // the measurement with the rule's own 200 MB. Nothing is raised, as with no clear.
        const int clearAfter = 200;
        int measured = ClearWorld.MeasuredAt(clearAfter), burst = burstAfter == 0 ? 20 : measured + burstAfter;
        long Flat(int i) => 500 * MB + Math.Clamp(i - burst, 0, 6) * 170 * MB / 6;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 360; i++)
        {
            a.Sample(GB, ("Reff", i > clearAfter ? 300 * MB : 600 * MB), ("Flat", Flat(i)));
            b.Sample(GB, ("Reff", 600 * MB), ("Flat", Flat(i)));
            if (i == clearAfter) a.Clear();
        }
        var flat = PoolTag.Parse("Flat");
        for (int i = 0; i < 360; i++)
            if (!a.Holding[i])
            {
                Assert.Equal(b.Alerts[i].Where(x => x.Tag == flat), a.Alerts[i].Where(x => x.Tag == flat));
                Assert.Equal(StateOf(b, i, flat), StateOf(a, i, flat));
            }
        Assert.Empty(a.Alerts.SelectMany(x => x));
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
    }

    [Fact]
    public void ABurstAClearLaterFreesIsNoFindingWhereItWasNone()
    {
        // Brst grows 180 MB in one minute and then holds; five minutes later PoolWatch's clear frees 300 MB of it. 180 MB is
        // under the 10-minute rule's 200 MB: nothing is raised, at the clear's measurement or after, as with no clear.
        const int clearAfter = 200, burst = clearAfter - 36;
        long Brst(int i) => 400 * MB + Math.Clamp(i - burst, 0, 6) * 30 * MB;
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 330; i++)
        {
            a.Sample(GB, ("Brst", Brst(i) - (i > clearAfter ? 300 * MB : 0)));
            b.Sample(GB, ("Brst", Brst(i)));
            if (i == clearAfter) a.Clear();
        }
        Assert.Empty(b.Alerts.SelectMany(x => x));
        Assert.Empty(a.Alerts.SelectMany(x => x));
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
    }

    [Theory]
    [InlineData(18, 234)] // 18 MB a minute (under both rules) and a 39-minute sleep before the clear is measured
    [InlineData(25, 42)]  // 25 MB a minute and a 7-minute sleep
    public void ASleepInsideAClearsIntervalIsReadAsItIsWithNoClear(int mbPerMinute, int sleepSamples)
    {
        // Reff grows while the PC is awake. PoolWatch clears the cache (it frees 100 MB of Reff), the PC sleeps a few seconds
        // later, before the clear is measured, then wakes and samples again. The clear's interval runs to the first sample
        // after waking and holds no sample: asleep nothing grows, so after waking the same rules fire, and the same incidents
        // open, as for the same sleep with no clear.
        const int awake = 240, after = 30;
        using var a = new ClearWorld(new Settings());
        using var c = new ClearWorld(new Settings());
        long L(int i) => 100 * MB + i * mbPerMinute * MB / 6;
        for (int i = 0; i < awake; i++) { a.Sample(GB, ("Reff", L(i))); c.Sample(GB, ("Reff", L(i))); }
        a.Clear();
        a.Index += sleepSamples;
        c.Index += sleepSamples;
        for (int k = 0; k < after; k++)
        {
            a.Sample(GB, ("Reff", L(awake - 1 + k) - 100 * MB));
            c.Sample(GB, ("Reff", L(awake - 1 + k)));
        }
        Assert.False(a.Holding[awake]); // measured at the first sample after waking
        int first = a.History.Count - after; // the first sample after waking, as the history (which keeps 70 minutes) holds it
        Assert.Equal(new ClearSpan(first - 1, first), Assert.Single(a.Detector.Clears.SpansIn(a.History).Spans));
        for (int i = awake; i < awake + after; i++) Assert.Equal(Fired(c.Alerts[i]), Fired(a.Alerts[i]));
        Assert.Equal(c.Incidents.CreatedCount, a.Incidents.CreatedCount);
        AssertRulesUnchanged(a);
    }

    [Theory]
    [InlineData(4)]    // a page lower from the clear's measurement on, as a series moves between two samples
    [InlineData(2048)] // 2 MB lower
    public void AWobbleAcrossAClearRaisesNothingTheSameCurveWithNoClearDoesNot(int lowerKb)
    {
        // Wobl climbs 21 MB a minute for 10 minutes up to the clear and then holds, lowerKb lower from the clear's measurement
        // (Reff, dropped 300 MB, makes it PoolWatch's clear). The 10 minutes before the measurement hold 7.8 minutes of the
        // climb (164 MB, under the rule): nothing is raised there or after it, as with no clear. The clear's drop of Wobl is
        // added back, so a window across the clear reads lowerKb more than the same curve with no clear does: a page changes
        // no state; 2 MB can keep the growing state a sample longer at its edge, as Wobl's own fall in the clear's 2 minutes is
        // the clear's.
        const int clearAfter = 200;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        long Wobl(int i) => 300 * MB + Math.Clamp(i - (clearAfter - 60), 0, 60) * 21 * MB / 6 - (i >= measured ? lowerKb * 1024L : 0);
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < 300; i++)
        {
            a.Sample(GB, ("Reff", i > clearAfter ? 300 * MB : 600 * MB), ("Wobl", Wobl(i)));
            b.Sample(GB, ("Reff", 600 * MB), ("Wobl", Wobl(i)));
            if (i == clearAfter) a.Clear();
        }
        var wobl = PoolTag.Parse("Wobl");
        bool Raised(ClearWorld w, int i) => w.Alerts[i].Any(x => x.Tag == wobl && x.Window == TimeSpan.FromMinutes(10));
        Assert.True(Raised(b, clearAfter));             // the climb's last sample: 10 minutes of it
        Assert.False(Raised(b, measured));              // 7.8 minutes of it in the window
        for (int i = measured; i < 300; i++)
        {
            Assert.DoesNotContain(a.Alerts[i], x => x.Tag == wobl);
            Assert.DoesNotContain(b.Alerts[i], x => x.Tag == wobl);
            if (lowerKb < 1024) Assert.Equal(StateOf(b, i, wobl), StateOf(a, i, wobl));
        }
        Assert.Equal(b.Incidents.CreatedCount, a.Incidents.CreatedCount);
        AssertRulesUnchanged(a);
    }

    // ------------------------------------------------------------------ a retained tag a clear frees

    [Theory]
    [InlineData(400, true)]  // to 600 MB, under its 800 MB floor: the memory was the cache's
    [InlineData(100, false)] // to 900 MB, still over it: still retained, until it falls under its floor on its own later
    public void AClearThatTakesARetainedTagUnderItsFloorSaysSo(int freedMb, bool ends)
    {
        // Reff holds 1 GB, flat, and is marked retained (floor 800 MB). PoolWatch's clear frees freedMb of it. Where that takes
        // it under its floor, it no longer counts as retained, and the detector says the clear did it; the same tag with no
        // clear stays retained. Twenty samples after the clear is measured, Reff falls 200 MB on its own: that is no clear's.
        const int clearAfter = 100;
        int measured = ClearWorld.MeasuredAt(clearAfter);
        using var a = new ClearWorld(new Settings());
        using var b = new ClearWorld(new Settings());
        for (int i = 0; i < measured + 30; i++)
        {
            if (i == 10)
            {
                a.Detector.MarkRetained(Reff, ClearWorld.T0, 800 * MB);
                b.Detector.MarkRetained(Reff, ClearWorld.T0, 800 * MB);
            }
            a.Sample(GB, ("Reff", (i <= clearAfter ? GB : GB - freedMb * MB) - (i >= measured + 20 ? 200 * MB : 0)));
            b.Sample(GB, ("Reff", GB));
            if (i == clearAfter) a.Clear();
            if (i == measured && ends) Assert.Equal([new ClearedRetention(Reff, freedMb * MB, a.Clears[0].TimeUtc)], a.Detector.ClearedRetentions);
            else Assert.Empty(a.Detector.ClearedRetentions);
        }
        Assert.Equal(TagState.Retained, a.Status[clearAfter][Reff].State);
        Assert.Equal(TagState.Retained, b.Status[measured][Reff].State);
        Assert.Equal(ends ? TagState.Normal : TagState.Retained, a.Status[measured][Reff].State);
        Assert.Equal(0, a.Status[measured][Reff].Delta10m); // the stitch says nothing changed
        Assert.NotEqual(TagState.Retained, a.Status[measured + 20][Reff].State); // its own fall ends it either way, unsaid
    }

    /// <summary>
    /// The incident test's retention scenario (growth, quiet, then File keeps its memory a minute after leaker.exe went quiet),
    /// with PoolWatch's clears: one during the growth that frees <paramref name="duringMb"/> of File, and one 11 minutes after
    /// the retention is found (its growth is then out of every 10-minute window) that frees <paramref name="laterMb"/>. With
    /// the incident's report.md as written at the end, and the log.
    /// </summary>
    private static (Incident Incident, Detector Detector, DateTime? Later, string Report, List<string> Logs) RetentionWithClears(int duringMb, int laterMb)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-ownclear-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settle = TimeSpan.FromSeconds(20);
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 1, ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
            var w = new World();
            var host = new FakeHost(dir);
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            var clears = new List<ClearRecord>();
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long file = 50 * MB, freed = 0;
            int laterAt = -1;
            DateTime? later = null;
            for (int i = 0; i < 520; i++)
            {
                if (i >= 15 && i < 55) { w.Io(100, other: 2000, handles: 2000); file += 200_000; }
                if (duringMb > 0 && i == 26) freed += duringMb * MB;
                if (laterAt > 0 && i == laterAt + 1) freed += laterMb * MB;
                w.Sample(("File", file - freed));
                if (i == 14 || (i >= 15 && i < 55 && i % 5 == 4)) w.ScanHandles();
                var now = w.Now - w.Interval;
                mgr.Process(w.History, det, det.Evaluate(w.History, new OwnClears(clears, settle)), now);
                ClearRecord Ran() => new() { TimeUtc = now.AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default };
                if (duringMb > 0 && i == 25) clears.Add(Ran());
                if (laterAt < 0 && mgr.All.Any(x => x.Retention is not null)) laterAt = i + 330;
                if (i == laterAt && laterMb > 0) { clears.Add(Ran()); later = clears[^1].TimeUtc; }
            }
            var inc = Assert.Single(mgr.All, x => x.Retention is not null);
            return (inc, det, later, File.ReadAllText(Path.Combine(inc.Dir, "report.md")), host.Logs);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void ARetentionsNoteSaysWhatPoolWatchsOwnClearsFreedOfItsRise()
    {
        // A clear during the growth freed part of File: its rise counts that back, and the note says it as the clear's.
        var (inc, _, _, _, _) = RetentionWithClears(duringMb: 4, laterMb: 0);
        Assert.NotNull(inc.OwnClearsFreed);
        Assert.StartsWith("PoolWatch's own cache clear at ", inc.OwnClearsFreed);
        Assert.EndsWith($" went quiet; {inc.OwnClearsFreed} of it", inc.Retention!.Note);
        // With no clear, the note is as it was.
        var (plain, _, _, _, _) = RetentionWithClears(duringMb: 0, laterMb: 0);
        Assert.EndsWith(" went quiet", plain.Retention!.Note);
    }

    [Theory]
    [InlineData(12, true)] // takes File under its floor: no longer retained, and its incident says the clear freed it
    [InlineData(2, false)] // leaves it over its floor: still retained, nothing said
    public void ARetentionAClearEndsIsSaidAsTheClearsResult(int laterMb, bool ends)
    {
        var (inc, det, later, report, logs) = RetentionWithClears(duringMb: 0, laterMb: laterMb);
        Assert.NotNull(later);
        var file = PoolTag.Parse("File");
        Assert.Equal(ends ? TagState.Normal : TagState.Retained, det.Status[file].State);
        string said = $"no longer retained: PoolWatch's own cache clear at {Fmt.Clock(later!.Value)} freed {laterMb} MB of it, so that memory was the cache's";
        Assert.Equal(ends, inc.Timeline.Any(t => t.Text == said));
        // Written to the incident's report on disk, and logged, not only kept on the incident in memory.
        Assert.Equal(ends, report.Contains(said, StringComparison.Ordinal));
        Assert.Equal(ends, logs.Any(l => l.EndsWith($"incident {inc.Id}: {said}", StringComparison.Ordinal)));
        if (!ends) Assert.DoesNotContain(inc.Timeline, t => t.Text.StartsWith("no longer retained", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ odds and ends of the incidents

    [Fact]
    public void AReopenedIncidentCountsItsQuietMinutesFromItsOwnRises()
    {
        // The rising case of the quiet theory, then Grow climbs again 20 minutes after its incident closed: the incident
        // re-opens, and that episode's quiet minutes count from its own rises, not from the earlier clear's measurement.
        const int clearAfter = 200;
        int stopAt = clearAfter - 6, measured = ClearWorld.MeasuredAt(clearAfter), again = measured + 120;
        long Grow(int i) => 100 * MB + Math.Min(i, stopAt) * 5 * MB - (i > clearAfter ? 300 * MB : 0) + Math.Clamp(i - again, 0, 60) * 5 * MB;
        using var a = new ClearWorld(new Settings());
        var grow = PoolTag.Parse("Grow");
        Incident? inc = null;
        for (int i = 0; i < again + 70; i++)
        {
            a.Sample(GB, ("Grow", Grow(i)));
            if (i == clearAfter) a.Clear();
            inc ??= a.Incidents.All.FirstOrDefault(x => x.Tag == grow);
            if (i == measured) Assert.Equal(a.History.Times[measured], inc!.QuietFromUtc);
        }
        Assert.Equal(1, inc!.Reopened);
        Assert.True(inc.IsOpen);
        Assert.Null(inc.QuietFromUtc);
    }

    [Fact]
    public void AClearMeasuredAtTheOnsetIsNotOneOfTheClearsSaid()
    {
        // Mark holds 600 MB; clear 1 drops it to 300 MB, and from its measurement it climbs (100 MB at once, then 30 MB a
        // minute): the onset is that measurement. Clear 2 later frees part of it: the one clear said, with its time.
        const int c1 = 100, c2 = 160;
        int m1 = ClearWorld.MeasuredAt(c1);
        long Mark(int i) => (i <= c1 ? 600 * MB : i <= m1 ? 300 * MB : 400 * MB + 5 * MB * (i - m1 - 1)) - (i > c2 ? 100 * MB : 0);
        using var a = new ClearWorld(new Settings { IncidentMaxMinutes = 600 });
        var mark = PoolTag.Parse("Mark");
        for (int i = 0; i < 200; i++)
        {
            a.Sample(GB, ("Mark", Mark(i)));
            if (i == c1 || i == c2) a.Clear();
        }
        var inc = Assert.Single(a.Incidents.Open, x => x.Tag == mark);
        a.Incidents.Rank(a.History, inc, a.Last);
        Assert.Equal(a.History.Times[m1], inc.OnsetUtc);
        Assert.Equal(a.Clears[1].TimeUtc, inc.ClearedAtUtc);
        Assert.StartsWith($"PoolWatch's own cache clear at {Fmt.Clock(a.Clears[1].TimeUtc)} freed ", inc.OwnClearsFreed);
    }
}
