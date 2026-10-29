using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Tokens;

namespace PoolWatch.Tests;

public class TokenTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public readonly ProcessRegistry Registry = new();
        public readonly TokenCorrelator Tokens;
        public uint Allocs = 100_000, Frees = 90_000;
        public DateTime Now = T0;
        public int Pid = 2000;
        public readonly List<Finding> Findings = new();

        public Rig()
        {
            Tokens = new TokenCorrelator(new Settings { TokenBucketSeconds = 10, TokenWindowMinutes = 30, TokenMinBuckets = 10, TokenMinOutstandingRise = 100 }, Registry);
            Registry.OnStart(600, 4, T0.AddHours(-1).ToFileTimeUtc(), "Codex.exe", null, null, null, T0.AddHours(-1));
            Registry.OnStart(601, 4, T0.AddHours(-1).ToFileTimeUtc(), "Other.exe", null, null, null, T0.AddHours(-1));
            Registry.DrainEvents();
        }

        /// <summary>One 10 s bucket: <paramref name="starts"/> children of PID 600 (and <paramref name="other"/> of PID 601), Toke outstanding + <paramref name="leak"/> (which may be negative).</summary>
        public void Bucket(int starts, int leak, int other = 0)
        {
            for (int i = 0; i < starts + other; i++) Registry.OnStart(Pid++, i < starts ? 600 : 601, Now.ToFileTimeUtc(), "git.exe", null, null, null, Now);
            var events = Registry.DrainEvents();
            Allocs += (uint)(starts + other + Math.Max(0, leak));
            Frees += (uint)(starts + other + Math.Max(0, -leak));
            var tags = new List<PoolTagEntry> { new(PoolTag.Parse("Toke"), Allocs, Frees, (Allocs - Frees) * 2048UL, 0, 0, 0), new(PoolTag.Parse("SeAt"), 50, 10, 4096, 0, 0, 0) };
            Now = Now.AddSeconds(10);
            if (Tokens.Observe(new Sample(Now, default, tags, []), events) is { } f) Findings.Add(f);
        }
    }

    [Fact]
    public void LeakPerStartTracksStartsAndNamesParent()
    {
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        foreach (int n in pattern) rig.Bucket(n, n, other: 3); // every Codex child leaks one token; Other's do not
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.TracksStarts, a.Verdict);
        Assert.Equal(pattern.Sum(), a.TokeOutstandingDelta);
        Assert.InRange(a.PerStart, 0.7, 1.0);
        Assert.Equal("Codex", a.Owners[0].Name); // per owner (the app), not per process
        Assert.True(a.Owners[0].Spearman > 0.9);
        var f = Assert.Single(rig.Findings);
        Assert.Equal(FindingKind.TokenSpawn, f.Kind);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("track process starts", f.Title);
    }

    [Fact]
    public void SteadyLeakWithoutStartsIsIndependent()
    {
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] starts = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        foreach (int n in starts) rig.Bucket(n, 50); // Toke climbs 50 per bucket whatever the starts do
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.IndependentOfStarts, a.Verdict);
        Assert.Contains("not a per-start leak", a.Summary);
        Assert.Equal(FindingSeverity.Info, Assert.Single(rig.Findings).Severity);
    }

    [Fact]
    public void AWeakFirstWindowDoesNotRuleOutALeakThatThenTracksStarts()
    {
        // The first 10 buckets (the first verdict) begin before the leak: services start and Toke rises with few starts.
        // Then the leak runs at a steady rate that every bucket catches about half of, so the starts barely vary and
        // unrelated token churn decides the order (r = 0.12 over the first 10 buckets). Then sharp on / off starts.
        var rig = new Rig();
        rig.Bucket(0, 0);
        rig.Bucket(0, 95, other: 19);
        rig.Bucket(0, 55, other: 4);
        rig.Bucket(0, -26, other: 5);
        int[] starts = [48, 32, 49, 32, 44, 34, 52], rises = [58, 76, 64, 88, 94, 82, 70];
        for (int i = 0; i < starts.Length; i++) rig.Bucket(starts[i], rises[i], other: 1);
        var first = rig.Tokens.Latest!;
        Assert.Equal(10, first.Buckets);
        Assert.InRange(first.Spearman, 0.1, 0.13);
        Assert.Equal(TokenVerdict.Unclear, first.Verdict); // not "not a per-start leak": 10 buckets at r = 0.12 cannot rule it out
        Assert.Empty(rig.Findings);
        for (int i = 0; i < 12; i++) rig.Bucket(i % 2 == 0 ? 60 : 0, i % 2 == 0 ? 120 : 0, other: 1);
        var f = Assert.Single(rig.Findings); // only the true verdict, never "not a per-start leak" first
        Assert.Equal("TracksStarts", f.Data["verdict"]);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
    }

    [Fact]
    public void ASteadyStreamOfStartsThatEachLeakIsNotCalledIndependent()
    {
        // A poller: about 30 starts in every bucket, each leaking one token, under churn larger than the starts vary.
        // The correlation is low, but a per-start leak at this rate could not have shown in it.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] wobble = [0, 1, 0, 2, 1, 0, 1, 2, 0, 1];
        for (int k = 0; k < 40; k++)
        {
            int n = 30 + wobble[k % 10];
            rig.Bucket(n, n + (k * 37 + 11) % 51 - 25);
        }
        var a = rig.Tokens.Latest!;
        Assert.True(a.Spearman < 0.3, $"r = {a.Spearman}");
        Assert.InRange(a.PerStart, 0.9, 1.1);
        Assert.Equal(TokenVerdict.Unclear, a.Verdict);
        Assert.DoesNotContain("not a per-start leak", a.Summary);
        Assert.Empty(rig.Findings);
    }

    [Fact]
    public void IdenticalBucketsCannotTellAPerStartLeakFromASteadyRise()
    {
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 12; i++) rig.Bucket(30, 30); // 30 starts and 30 more tokens in every bucket: r is NaN
        Assert.Equal(TokenVerdict.Unclear, rig.Tokens.Latest!.Verdict);
        Assert.Empty(rig.Findings);
    }

    [Fact]
    public void TokeRisingWithNoStartsAtAllIsIndependent()
    {
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 12; i++) rig.Bucket(0, 50);
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.IndependentOfStarts, a.Verdict);
        Assert.Contains("not a per-start leak", a.Summary);
        Assert.Equal(FindingSeverity.Info, Assert.Single(rig.Findings).Severity);
    }

    [Fact]
    public void ANoisySteadyRiseWithVaryingStartsIsStillIndependent()
    {
        // Starts swing from 0 to 40 a bucket while Toke climbs 40 to 60 a bucket regardless: enough buckets rule a
        // per-start leak out (from the 21st here; not from the first 10).
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] starts = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        for (int k = 0; k < 30; k++) rig.Bucket(starts[k % 12], 50 + (k * 29 + 7) % 21 - 10);
        Assert.Equal(TokenVerdict.IndependentOfStarts, rig.Tokens.Latest!.Verdict);
        var f = Assert.Single(rig.Findings);
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Equal(21, f.Data["buckets"]);
    }

    [Fact]
    public void ALowCorrelationIsUnclearWhenChurnThisLargeWouldHideALeakAtThisRate()
    {
        // Starts swing from 0 to 40 a bucket and Toke rises about 24 a bucket on average, but swings from -64 to +112
        // regardless. Over 30 buckets r is near zero and its one-sided bound (0.30) is well under the tracking bar, yet a
        // leak of the window's 1.8 tokens a start would only reach a correlation of about 0.45 against churn this large:
        // under the tracking bar, so the low r rules nothing out. A power bar of 0.3 would call this "not a per-start
        // leak" from the 14th bucket.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        int[] starts = new int[30], rises = new int[30];
        for (int k = 0; k < 30; k++)
        {
            starts[k] = pattern[k % 12];
            rises[k] = (k * 25 + 4) % 187 - 68;
            rig.Bucket(starts[k], rises[k]);
        }
        var a = rig.Tokens.Latest!;
        Assert.Equal(30, a.Buckets);
        Assert.InRange(a.Spearman, -0.05, 0.05);
        Assert.InRange(a.PerStart, 1.7, 1.9);
        Assert.InRange(LeakPower(a.PerStart, starts, rises), 0.43, 0.46);
        Assert.Equal(TokenVerdict.Unclear, a.Verdict);
        Assert.Empty(rig.Findings);
    }

    [Fact]
    public void ACorrelationJustOverTheNoLinkBarIsUnclearEvenOverThirtyBuckets()
    {
        // Toke climbs 31 to 68 a bucket with a weak link to the starts: r = 0.33 over 30 buckets, and a leak at this rate
        // would have shown plainly. The one-sided bound (0.58) is under the tracking bar, so only the 0.3 bar keeps this
        // from being called "not a per-start leak": a correlation of 0.33 is a link, however weak.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        int[] starts = new int[30], rises = new int[30];
        for (int k = 0; k < 30; k++)
        {
            starts[k] = pattern[k % 12];
            rises[k] = 31 + (k * 11 + 15) % 39;
            rig.Bucket(starts[k], rises[k]);
        }
        var a = rig.Tokens.Latest!;
        Assert.Equal(30, a.Buckets);
        Assert.InRange(a.Spearman, 0.31, 0.35);
        Assert.True(LeakPower(a.PerStart, starts, rises) > 0.9);
        Assert.Equal(TokenVerdict.Unclear, a.Verdict);
        Assert.Empty(rig.Findings);
    }

    /// <summary>
    /// The correlation a leak of <paramref name="perStart"/> tokens a start would give against churn as large as the
    /// rises' own spread (the power check in the correlator): pins where a series sits against the 0.6 tracking bar.
    /// </summary>
    private static double LeakPower(double perStart, int[] starts, int[] rises)
    {
        double x = perStart * Sd(starts) / Sd(rises);
        return x / Math.Sqrt(1 + x * x);

        static double Sd(int[] v)
        {
            double mean = v.Average();
            return Math.Sqrt(v.Sum(y => (y - mean) * (y - mean)) / v.Length);
        }
    }

    [Fact]
    public void BalancedStartsAreFlatAndNotReported()
    {
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 15; i++) rig.Bucket(i % 3 * 20, 0);
        Assert.Equal(TokenVerdict.Flat, rig.Tokens.Latest!.Verdict);
        Assert.Empty(rig.Findings);
    }

    [Fact]
    public void OutstandingSurvivesCounterWrap()
    {
        var e = new PoolTagEntry(PoolTag.Parse("Toke"), 5, uint.MaxValue - 4, 0, 3, 1, 0); // allocs wrapped past frees
        Assert.Equal(10 + 2, TokenCorrelator.Outstanding(e));
    }

    [Fact]
    public void TokenFamilyIsBuiltIn()
    {
        foreach (var t in new[] { "Toke", "SeAt", "SeTd", "SeDt", "SeTl" })
        {
            Assert.True(BuiltinTags.Table.ContainsKey(PoolTag.Parse(t)), t);
            Assert.Equal("nt!se", BuiltinTags.Table[PoolTag.Parse(t)].Owner);
            Assert.Contains(PoolTag.Parse(t), BuiltinTags.AlwaysTracked);
        }
    }

    [Fact]
    public void MinBucketsFitTheWindow()
    {
        var s = new Settings { TokenBucketSeconds = 600, TokenWindowMinutes = 60, TokenMinBuckets = 10 };
        s.Normalize();
        Assert.Equal(5, s.TokenMinBuckets); // 6 buckets fit in the window: the first is the baseline point
    }
}
