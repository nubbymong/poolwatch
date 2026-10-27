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

        /// <summary>One 10 s bucket: <paramref name="starts"/> children of PID 600 (and <paramref name="other"/> of PID 601), Toke outstanding + <paramref name="leak"/>.</summary>
        public void Bucket(int starts, int leak, int other = 0)
        {
            for (int i = 0; i < starts + other; i++) Registry.OnStart(Pid++, i < starts ? 600 : 601, Now.ToFileTimeUtc(), "git.exe", null, null, null, Now);
            var events = Registry.DrainEvents();
            Allocs += (uint)(starts + other + leak);
            Frees += (uint)(starts + other);
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
