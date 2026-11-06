using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Tokens;
using static PoolWatch.Tests.Clocks;

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
        /// <summary>How far the wall clock (the samples' time) is set from <see cref="Now"/>, which the clock that only goes forward follows.</summary>
        public TimeSpan WallShift = TimeSpan.Zero;
        public int Pid = 2000;
        public readonly List<Finding> Findings = new();
        private readonly int _seconds;

        public Rig() : this(new Settings { TokenBucketSeconds = 10, TokenWindowMinutes = 30, TokenMinBuckets = 10, TokenMinOutstandingRise = 100 }, 10) { }

        /// <summary>A rig on <paramref name="settings"/>, whose buckets are <paramref name="seconds"/> long.</summary>
        public Rig(Settings settings, int seconds)
        {
            _seconds = seconds;
            Tokens = new TokenCorrelator(settings, Registry);
            Registry.OnStart(600, 4, T0.AddHours(-1).ToFileTimeUtc(), "Codex.exe", null, null, null, T0.AddHours(-1));
            Registry.OnStart(601, 4, T0.AddHours(-1).ToFileTimeUtc(), "Other.exe", null, null, null, T0.AddHours(-1));
            Registry.DrainEvents();
        }

        /// <summary>
        /// One bucket: <paramref name="starts"/> children of <paramref name="parent"/> (PID 600, Codex, unless another is given;
        /// a PID the registry does not know is a parent PoolWatch cannot name), and <paramref name="other"/> of PID 601, with
        /// Toke outstanding + <paramref name="leak"/> (which may be negative).
        /// </summary>
        public void Bucket(int starts, int leak, int other = 0, int parent = 600)
        {
            for (int i = 0; i < starts + other; i++) Registry.OnStart(Pid++, i < starts ? parent : 601, Now.ToFileTimeUtc(), "git.exe", null, null, null, Now);
            var events = Registry.DrainEvents();
            Allocs += (uint)(starts + other + Math.Max(0, leak));
            Frees += (uint)(starts + other + Math.Max(0, -leak));
            var tags = new List<PoolTagEntry> { new(PoolTag.Parse("Toke"), Allocs, Frees, (Allocs - Frees) * 2048UL, 0, 0, 0), new(PoolTag.Parse("SeAt"), 50, 10, 4096, 0, 0, 0) };
            Now = Now.AddSeconds(_seconds);
            if (Tokens.Observe(new Sample(Now + WallShift, default, tags, []) { TickMs = Clocks.At(Now + WallShift).Ms }, events, (long)(Now - T0).TotalMilliseconds) is { } f) Findings.Add(f);
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
    public void TheVerdictNamesTheOwnerWhoseStartsTrackTokeNotTheOneWithTheMostStarts()
    {
        // Another app starts 20 processes in every bucket, more than Codex's 15 a bucket on average, and none of them leaves a
        // token; each of Codex's leaves one. Toke tracks the starts, and the verdict and its finding name Codex, whose starts
        // rise and fall with Toke, not the app with the most starts (it named that one, "Other" here).
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        for (int i = 0; i < 24; i++) rig.Bucket(pattern[i % 12], pattern[i % 12], other: 20);
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.TracksStarts, a.Verdict);
        Assert.True(a.Owners.Single(o => o.Name == "Other").Starts > a.Owners.Single(o => o.Name == "Codex").Starts, a.Summary);
        Assert.Equal("Codex", a.Owners[0].Name);
        Assert.Contains("; the starts that rise and fall with Toke most: Codex's (", a.Summary);
        Assert.DoesNotContain("Other", a.Summary);
        Assert.Equal("Codex", Assert.Single(rig.Findings).Image);
        // Named picks none when no owner's starts move with Toke at least somewhat (one with no correlation at all
        // included), and none that PoolWatch could not name.
        Assert.Null(TokenCorrelator.Named([new OwnerTokenStat("app:other", "Other", 900, 0.6, double.NaN), new OwnerTokenStat("app:codex", "Codex", 600, 0.4, 0.2)]));
        Assert.Null(TokenCorrelator.Named([new OwnerTokenStat("pid:9", "PID 9", 900, 0.6, 0.95, Core.Identity.OwnerKind.Unknown)]));
    }

    [Fact]
    public void AFindingWhoseStartsNoProgramsMoveWithTokeNamesNoProgram()
    {
        // Toke climbs 50 a bucket whatever Codex's starts do: "not a per-start leak", and Codex, the program with every
        // start, is not given as the program behind it (its starts do not move with Toke).
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] starts = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        foreach (int n in starts) rig.Bucket(n, 50);
        var f = Assert.Single(rig.Findings);
        Assert.Equal("IndependentOfStarts", f.Data["verdict"]);
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        Assert.Null(f.Image);
    }

    [Fact]
    public void AParentPoolWatchCannotNameIsNeverGivenAsTheProgramBehindALeak()
    {
        // The leaking starts are children of PID 9999, a parent the registry does not know (its ancestry cannot be read):
        // each leaves a token, while the other app starts 20 a bucket that leave none. Toke tracks the starts; the summary
        // says the starts that move with Toke are made by a process PoolWatch could not name, and the finding gives no
        // program.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        for (int i = 0; i < 24; i++) rig.Bucket(pattern[i % 12], pattern[i % 12], other: 20, parent: 9999);
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.TracksStarts, a.Verdict);
        var unknown = Assert.Single(a.Owners, o => o.Name == "PID 9999");
        Assert.Equal(Core.Identity.OwnerKind.Unknown, unknown.Kind);
        Assert.Contains("; the starts that rise and fall with Toke most are made by a process PoolWatch could not name (PID 9999, ", a.Summary);
        Assert.Null(TokenCorrelator.Named(a.Owners));
        Assert.Null(Assert.Single(rig.Findings).Image);
    }

    [Fact]
    public void AnAppWhoseStartsTrackTokeLessThanAParentPoolWatchCannotNameIsNotGivenInItsPlace()
    {
        // PID 9999's children leave a token each, as above; the other app's starts leave none but rise and fall partly with
        // them (a rank correlation of about 0.67 with Toke). The owner whose starts track Toke the most is PID 9999, which
        // says no program: the finding gives none, not the other app in its place, and the summary says the starts that
        // track are made by a process PoolWatch could not name.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0], other = [10, 20, 5, 30, 15, 25, 0, 10, 20, 5, 30, 15];
        for (int i = 0; i < 24; i++) rig.Bucket(pattern[i % 12], pattern[i % 12], other: other[i % 12], parent: 9999);
        var a = rig.Tokens.Latest!;
        Assert.Equal(TokenVerdict.TracksStarts, a.Verdict);
        var unknown = Assert.Single(a.Owners, o => o.Name == "PID 9999");
        var app = Assert.Single(a.Owners, o => o.Name == "Other");
        Assert.InRange(unknown.Spearman, 0.99, 1.0);
        Assert.InRange(app.Spearman, 0.6, 0.7);
        Assert.Equal(unknown, a.Owners[0]); // listed first, as the one whose starts track Toke the most
        Assert.Null(TokenCorrelator.Named(a.Owners));
        Assert.Contains("; the starts that rise and fall with Toke most are made by a process PoolWatch could not name (PID 9999, ", a.Summary);
        Assert.DoesNotContain("Other", a.Summary);
        var f = Assert.Single(rig.Findings);
        Assert.Null(f.Image);
        Assert.DoesNotContain("Other", f.Title);
        // Named ranks every owner, those PoolWatch could not name included: it gives an app only when the app's starts
        // track Toke the most.
        OwnerTokenStat pid = new("pid:9", "PID 9", 300, 0.5, 0.95, Core.Identity.OwnerKind.Unknown), codex = new("app:codex", "Codex", 300, 0.5, 0.65);
        Assert.Null(TokenCorrelator.Named([pid, codex]));
        Assert.Null(TokenCorrelator.Named([codex, pid]));
        Assert.Equal(codex, TokenCorrelator.Named([pid with { Spearman = 0.5 }, codex]));
    }

    [Fact]
    public void ALeakThatStartsAfterAQuietSpellIsReportedAsSoonAsAFreshStartWouldReportIt()
    {
        // Twenty minutes in which another app starts processes and none leaks (Toke flat), then Codex's children start to
        // leak a token each. The whole window still holds the quiet spell, whose starts left nothing: judged on it alone, the
        // leak would be reported late. The last bucket of the flat quiet spell is at or below every later reading, so a window
        // begins there, as a fresh start's would, and says so once it has the ten buckets a verdict needs.
        var rig = new Rig();
        rig.Bucket(0, 0);
        int[] pattern = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];
        for (int i = 0; i < 120; i++) rig.Bucket(0, 0, other: pattern[i % 12]);
        Assert.Empty(rig.Findings);
        int[] leak = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        int reportedAt = -1;
        for (int i = 0; i < leak.Length && reportedAt < 0; i++)
        {
            rig.Bucket(leak[i], leak[i], other: 3); // every Codex child leaks one token; the other app's do not
            if (rig.Findings.Count > 0) reportedAt = i + 1;
        }
        Assert.InRange(reportedAt, 1, 10); // within the ten buckets a fresh start needs
        var f = Assert.Single(rig.Findings);
        Assert.Equal("TracksStarts", f.Data["verdict"]);
        Assert.Equal(10, f.Data["buckets"]);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        Assert.NotEqual(TokenVerdict.TracksStarts, rig.Tokens.Analyse().Verdict); // the whole window alone would not say so yet
    }

    [Theory]
    [InlineData(-1)] // the wall clock set back an hour
    [InlineData(24)] // and forward a day
    public void AWallClockSetBackOrForwardChangesNoBucketNorTheWindowNorWhenAFindingRepeats(int hours)
    {
        // Two hours of quiet buckets in the test rig (its window holds half an hour), the wall clock changed twenty buckets
        // before their end, then the leak. Buckets close, leave the window and repeat a finding by the clock that only goes forward, so the
        // window holds as many buckets as with the clock left alone, the leak is warned of at the same bucket, and a day's
        // jump of the clock after the warning does not repeat it within the hour. Timed on the samples' clock, no bucket
        // closed for the hour after the clock was set back, the jump forward emptied the window, and the warning repeated.
        int[] leak = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        (int warnedAt, int heldBefore, int findings) Run(TimeSpan shift)
        {
            var rig = new Rig();
            rig.Bucket(0, 0);
            for (int i = 0; i < 720; i++)
            {
                if (i == 700) rig.WallShift = shift;
                rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
            }
            int held = rig.Tokens.Buckets.Count, at = -1;
            for (int i = 0; i < 60; i++)
            {
                rig.Bucket(leak[i % leak.Length], leak[i % leak.Length], other: 3);
                if (at < 0 && Warned(rig)) { at = i + 1; rig.WallShift += shift; }
            }
            return (at, held, rig.Findings.Count(f => (string?)f.Data["verdict"] == "TracksStarts"));
        }
        var steady = Run(TimeSpan.Zero);
        Assert.InRange(steady.warnedAt, 1, 10);
        Assert.Equal(181, steady.heldBefore); // half an hour of ten-second buckets, and the baseline point
        Assert.Equal(1, steady.findings);
        Assert.Equal(steady, Run(TimeSpan.FromHours(hours)));
    }

    /// <summary>Another app's starts, none of which leaks: a quiet spell's background.</summary>
    private static readonly int[] OtherStarts = [30, 0, 10, 0, 40, 5, 0, 20, 0, 35, 0, 15];

    private static bool Warned(Rig rig) => rig.Findings.Any(f => (string?)f.Data["verdict"] == "TracksStarts");

    /// <summary>
    /// The bucket (counted from the leak's first) at which a per-start leak of <paramref name="leak"/> (Codex's starts each
    /// bucket, each leaving a token) is first reported as tracking the starts, after <paramref name="quiet"/> buckets of
    /// <see cref="OtherStarts"/> with Toke flat; -1 if not within <paramref name="max"/> buckets.
    /// </summary>
    private static int FirstWarning(Rig rig, int quiet, int[] leak, int max = 200)
    {
        rig.Bucket(0, 0);
        for (int i = 0; i < quiet; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        Assert.False(Warned(rig));
        for (int i = 0; i < max; i++)
        {
            rig.Bucket(leak[i % leak.Length], leak[i % leak.Length], other: 3);
            if (Warned(rig)) return i + 1;
        }
        return -1;
    }

    [Fact]
    public void ASlowLeakThatStartsAfterAQuietSpellIsReportedNoLaterThanAtAFreshStart()
    {
        // Half the leak rate of the test above: a ten-bucket window of it rises less than the 100 a verdict needs, so judged
        // on ten buckets that hold a few quiet ones, or on the whole window, it was reported at the 128th leak bucket. The
        // window that begins at the last bucket of the quiet spell grows with the leak, as a fresh start's does.
        int[] half = [0, 10, 2, 20, 0, 15, 5, 0, 12, 7, 17, 0];
        int fresh = FirstWarning(new Rig(), quiet: 0, half);
        Assert.Equal(15, fresh);
        var rig = new Rig();
        int afterQuiet = FirstWarning(rig, quiet: 120, half);
        Assert.InRange(afterQuiet, 1, fresh);
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        Assert.NotEqual(TokenVerdict.TracksStarts, rig.Tokens.Analyse().Verdict); // the whole window alone would not say so yet
    }

    [Fact]
    public void ALeakAfterAQuietSpellIsReportedNoLaterThanAtAFreshStartWithALongerWindow()
    {
        // The default buckets (a minute) and thresholds, with a three-hour window (Settings allow up to a day), about 37
        // tokens a minute: reported at minute 50 after a 150-minute quiet spell when only the whole window and the last ten
        // minutes were judged; a fresh start reports it at minute 15.
        int[] leak = [0, 50, 12, 100, 0, 75, 25, 0, 62, 37, 87, 0];
        var settings = new Settings { TokenWindowMinutes = 180 };
        int fresh = FirstWarning(new Rig(settings, 60), quiet: 0, leak, max: 300);
        Assert.Equal(15, fresh);
        Assert.InRange(FirstWarning(new Rig(settings, 60), quiet: 150, leak, max: 300), 1, fresh);
    }

    [Fact]
    public void TheWarningStaysTheVerdictWhileTheLeakRunsOn()
    {
        // Once reported, the leak is still the verdict at every bucket while it runs on: windows that begin inside the leak say
        // so (each of 10 or 11 buckets, from a bucket before one of its rises), and then the whole window, once the leak fills it.
        var rig = new Rig();
        int[] leak = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        int reportedAt = FirstWarning(rig, quiet: 120, leak);
        Assert.InRange(reportedAt, 1, 10);
        for (int i = reportedAt; i < 120; i++)
        {
            rig.Bucket(leak[i % leak.Length], leak[i % leak.Length], other: 3);
            Assert.True(rig.Tokens.Latest!.Verdict == TokenVerdict.TracksStarts, $"leak bucket {i + 1}: {rig.Tokens.Latest.Verdict}");
        }
        Assert.Single(rig.Findings, f => (string?)f.Data["verdict"] == "TracksStarts"); // said once an hour, as before
    }

    [Fact]
    public void ALeakAfterAnEarlierRiseAndAQuietSpellIsJudgedOnItsOwnBuckets()
    {
        // Services starting at sign-in raise Toke by 1,000 in five buckets, with no starts PoolWatch saw; then ten minutes of
        // quiet; then the slow leak. The earlier rise is still in the window, so Toke was not flat since the window began,
        // yet the leak is judged from where the quiet spell ended.
        int[] half = [0, 10, 2, 20, 0, 15, 5, 0, 12, 7, 17, 0];
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 5; i++) rig.Bucket(0, 200);
        for (int i = 0; i < 60; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        int at = -1;
        for (int i = 0; i < 100 && at < 0; i++)
        {
            rig.Bucket(half[i % half.Length], half[i % half.Length], other: 3);
            if (Warned(rig)) at = i + 1;
        }
        Assert.InRange(at, 1, FirstWarning(new Rig(), quiet: 0, half));
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
    }

    /// <summary>
    /// The bucket (counted from the leak's first) at which <paramref name="leak"/> (Codex's starts each bucket, each leaving
    /// a token, with three of another app's) is first warned of, after <paramref name="before"/>: Toke's change in each
    /// bucket before the leak, with <see cref="OtherStarts"/>, none of which leaks; -1 if not within <paramref name="max"/>.
    /// </summary>
    private static int WarnedAt(Rig rig, int[] before, int[] leak, int max = 300)
    {
        rig.Bucket(0, 0);
        for (int i = 0; i < before.Length; i++) rig.Bucket(0, before[i], other: OtherStarts[i % OtherStarts.Length]);
        Assert.False(Warned(rig));
        for (int i = 0; i < max; i++)
        {
            rig.Bucket(leak[i % leak.Length], leak[i % leak.Length], other: 3);
            if (Warned(rig)) return i + 1;
        }
        return -1;
    }

    private static int[] Quiet(int buckets) => new int[buckets];

    /// <summary>A quiet spell in which Toke drops by <paramref name="depth"/> at bucket <paramref name="at"/> and comes back <paramref name="back"/> buckets later (0: never).</summary>
    private static int[] Dip(int buckets, int depth, int at, int back)
    {
        var t = new int[buckets];
        t[at] -= depth;
        if (back > 0) t[at + back] += depth;
        return t;
    }

    /// <summary>A quiet spell in which Toke wanders: up or down by as much as 22 in a bucket, back where it began every 23 buckets.</summary>
    private static int[] Uneven(int buckets) => Enumerable.Range(0, buckets).Select(i => (i * 7 % 23 - 11) * 2).ToArray();

    /// <summary>An earlier rise with no starts (services at sign-in, say): <paramref name="buckets"/> buckets of <paramref name="each"/>.</summary>
    private static int[] Rise(int buckets, int each) => Enumerable.Repeat(each, buckets).ToArray();

    private static readonly int[] PerMinute37 = [0, 50, 12, 100, 0, 75, 25, 0, 62, 37, 87, 0];

    /// <summary>What lies before the leak in <see cref="ALeakIsWarnedOfWithinTheBudgetOfAFreshStartWhateverCameBeforeIt"/>.</summary>
    private static int[] Before(string shape) => shape switch
    {
        "flat" => Quiet(45),
        "1-token dip early, back a bucket later" => Dip(45, 1, 5, 1),
        "30-token drop that stays" => Dip(45, 30, 5, 0),
        "100-token dip, back 10 buckets later" => Dip(45, 100, 10, 10),
        "100-token dip late, back 5 buckets before the leak" => Dip(45, 100, 35, 5),
        "uneven" => Uneven(45),
        "5 x +200, then 20 quiet" => [.. Rise(5, 200), .. Quiet(20)],
        "6 x +120, then 10 quiet" => [.. Rise(6, 120), .. Quiet(10)],
        "6 x +120, then 15 quiet" => [.. Rise(6, 120), .. Quiet(15)],
        "6 x +120, then 20 quiet" => [.. Rise(6, 120), .. Quiet(20)],
        "6 x +120, then 30 quiet" => [.. Rise(6, 120), .. Quiet(30)],
        "8 x +100, then 10 quiet" => [.. Rise(8, 100), .. Quiet(10)],
        "+500 then +50, then 10 quiet" => [500, 50, .. Quiet(10)],
        "a drop, then 5 x +200, then 20 uneven" => [.. Dip(10, 30, 2, 0), .. Rise(5, 200), .. Uneven(20)],
        _ => throw new ArgumentException(shape),
    };

    [Theory]
    [InlineData("flat")]
    [InlineData("1-token dip early, back a bucket later")]
    [InlineData("30-token drop that stays")]
    [InlineData("100-token dip, back 10 buckets later")]
    [InlineData("100-token dip late, back 5 buckets before the leak")]
    [InlineData("uneven")]
    [InlineData("5 x +200, then 20 quiet")]
    [InlineData("6 x +120, then 10 quiet")]
    [InlineData("6 x +120, then 15 quiet")]
    [InlineData("6 x +120, then 20 quiet")]
    [InlineData("6 x +120, then 30 quiet")]
    [InlineData("8 x +100, then 10 quiet")]
    [InlineData("+500 then +50, then 10 quiet")]
    [InlineData("a drop, then 5 x +200, then 20 uneven")]
    public void ALeakIsWarnedOfWithinTheBudgetOfAFreshStartWhateverCameBeforeIt(string shape)
    {
        // The default settings (a minute a bucket, a one-hour window, ten buckets, a rise of 500) and a leak of about 37
        // tokens a minute, each of Codex's starts leaving a token. The budget: a fresh start of PoolWatch as the leak begins
        // warns of it at its 15th bucket. Whatever lies before the leak in the window (a quiet spell that is flat, uneven or
        // dips, an earlier rise and then quiet buckets), the leak is judged from its own lowest reading and warned of within
        // that budget. With the earlier rule (a window from where a scan found a quiet spell's end), the leak after an earlier
        // rise was warned of at buckets 25 to 44 (seven of the eight), after a dip at 18 and 21 (two of four).
        int fresh = WarnedAt(new Rig(new Settings(), 60), [], PerMinute37);
        Assert.Equal(15, fresh);
        var rig = new Rig(new Settings(), 60);
        int at = WarnedAt(rig, Before(shape), PerMinute37);
        Assert.True(at >= 1 && at <= fresh, $"{shape}: warned at leak bucket {at}, budget {fresh}");
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
    }

    [Theory]
    [InlineData(6, 120)]
    [InlineData(5, 200)]
    [InlineData(8, 100)]
    [InlineData(10, 150)]
    [InlineData(15, 100)]
    [InlineData(20, 80)]
    [InlineData(10, 60)]
    [InlineData(12, 50)]
    [InlineData(10, 30)]
    public void ALeakStraightAfterAnEarlierRiseIsWarnedOfWithinTheBudgetOfAFreshStart(int buckets, int each)
    {
        // An earlier rise (services at sign-in, say: no starts PoolWatch saw), steeper than the leak or not, then none to
        // eight quiet buckets, then the leak of about 37 tokens a minute at the default settings. The bucket before the leak's
        // first rise is at or below every later reading, so the leak is judged from there, as a fresh start there would judge
        // it, and warned of within the fresh start's 15 buckets, the warning naming Codex. A rule that judged no window from
        // a bucket Toke was rising into warned of it at buckets 18 to 53 after a rise of 50 a minute or more and none to four
        // quiet buckets, and as late as 18 after five to eight.
        foreach (int quiet in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var rig = new Rig(new Settings(), 60);
            int at = WarnedAt(rig, [.. Rise(buckets, each), .. Quiet(quiet)], PerMinute37);
            Assert.True(at >= 1 && at <= 15, $"{buckets} x +{each}, then {quiet} quiet: warned at leak bucket {at}, budget 15");
            Assert.Equal("Codex", Assert.Single(rig.Findings, f => (string?)f.Data["verdict"] == "TracksStarts").Image);
        }
    }

    [Fact]
    public void ASlowLeakAfterAFasterDriftOrASteeperRiseIsWarnedOfWithinTheBudgetOfAFreshStart()
    {
        // A leak of about 18 tokens a minute (a fresh start warns of it at its 28th bucket) that follows a drift of 20 a
        // minute with no starts behind it, so Toke's rate barely changes where the leak begins, or follows a steeper rise
        // straight on or after five quiet buckets. A rule that judged no window from a bucket Toke was rising into warned of
        // these at buckets 34, 36 and 35.
        int[] perMinute18 = PerMinute37.Select(x => x / 2).ToArray();
        int fresh = WarnedAt(new Rig(new Settings(), 60), [], perMinute18);
        Assert.Equal(28, fresh);
        foreach (var before in new[] { Rise(30, 20), Rise(6, 120), [.. Rise(6, 120), .. Quiet(5)] })
        {
            var rig = new Rig(new Settings(), 60);
            int at = WarnedAt(rig, before, perMinute18);
            Assert.True(at >= 1 && at <= fresh, $"warned at leak bucket {at}, budget {fresh}");
            Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        }
    }

    /// <summary>
    /// Readings that wander around one level by up to <paramref name="amp"/> either way (a different, fixed draw for each
    /// <paramref name="seed"/>), as Toke's change per bucket: back at the level by the last bucket.
    /// </summary>
    private static int[] Wander(int buckets, int amp, int seed)
    {
        var level = new int[buckets + 1];
        uint x = (uint)(seed * 2654435761u + 12345);
        for (int i = 0; i < buckets; i++) { x = x * 1664525u + 1013904223u; level[i] = (int)(x >> 8) % (2 * amp + 1) - amp; }
        level[buckets] = 0;
        var change = new int[buckets];
        change[0] = level[0];
        for (int i = 1; i < buckets; i++) change[i] = level[i] - level[i - 1];
        change[buckets - 1] = -level[buckets - 2];
        return change;
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void ALeakAfterQuietReadingsThatWanderIsWarnedOfWithinTheBudgetOfAFreshStart(int amp)
    {
        // Forty-five quiet buckets in which Toke's reading wanders by up to amp either way (short-lived processes holding a
        // token at the moment of a reading, say), then the leak of about 37 a minute at the default settings: warned of
        // within the fresh start's 15 buckets, whatever the draw. A rule that judged no window from a bucket Toke was rising
        // into (the wandering is a rise of up to twice amp) warned of it as late as bucket 45 when amp was 125 and at buckets
        // 39 to 46 when it was 200.
        for (int seed = 1; seed <= 6; seed++)
        {
            var rig = new Rig(new Settings(), 60);
            int at = WarnedAt(rig, Wander(45, amp, seed), PerMinute37);
            Assert.True(at >= 1 && at <= 15, $"wander {amp}, draw {seed}: warned at leak bucket {at}, budget 15");
            Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        }
    }

    [Theory]
    [InlineData(0, 10)] // lumpy: three minutes in ten Toke jumps by 50 to 200, otherwise it wanders by up to 20
    [InlineData(1, 10)] // smooth: 37 a minute, give or take 30
    [InlineData(2, 10)] // lumpy for half an hour, then wandering for half an hour, and so on
    [InlineData(3, 10)] // rarer, larger lumps: about one minute in seven Toke jumps by 100 to 400, otherwise it wanders by up to 30
    [InlineData(0, 4)] // the same with fewer buckets per verdict, as the Advanced settings allow (4 at the least)
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(2, 6)]
    [InlineData(3, 6)]
    public void RisesThatIgnoreTheStartsRaiseNoWarningFromAWindowInsideTheWindow(int shape, int minBuckets)
    {
        // Eight hours at the default settings, with ten buckets per verdict (the default) or fewer, forty draws of each shape:
        // Toke rises about 37 a minute on average while Codex starts 0 to 40 processes a minute and another app 0 to 9, none
        // of them in step with Toke. At every bucket close, a "tracks the starts" verdict, when there is one, is the whole
        // window's: no window that begins inside the window says so, however many are judged. Judged at the tracking bar
        // itself, those windows raised a false warning in 37, 36, 29 and 34 of the 40 runs of these four shapes, and in 26,
        // 1, 20 and 28 with the rule that judged none from a bucket Toke was rising into. With the bound, but windows inside
        // the window judged on as few buckets as a verdict needs, they raised one in 3, 0, 5 and 17 runs at 4 buckets, 1, 0,
        // 1 and 5 at 5, and 0, 0, 0 and 1 at 6. The whole window alone raises one in 0, 1, 0 and 1 of the runs at 10 buckets
        // (and at 7 to 9), 0, 1, 0 and 2 at 6, and 0, 1, 0 and 3 at 4 and 5.
        int judged = 0;
        var settings = new Settings { TokenMinBuckets = minBuckets };
        settings.Normalize();
        Assert.Equal(minBuckets, settings.TokenMinBuckets);
        for (int seed = 1; seed <= 40; seed++)
        {
            var rig = new Rig(settings, 60);
            var rnd = new Random(seed);
            rig.Bucket(0, 0);
            for (int i = 0; i < 480; i++)
            {
                int d = shape switch
                {
                    0 => rnd.NextDouble() < 0.3 ? rnd.Next(50, 201) : rnd.Next(-20, 21),
                    1 => 37 + rnd.Next(-30, 31),
                    2 => i / 30 % 2 == 0 ? (rnd.NextDouble() < 0.3 ? rnd.Next(50, 201) : rnd.Next(-20, 21)) : rnd.Next(-20, 21),
                    _ => rnd.NextDouble() < 0.15 ? rnd.Next(100, 401) : rnd.Next(-30, 31),
                };
                rig.Bucket(rnd.Next(0, 41), d, rnd.Next(0, 10));
                judged += rig.Tokens.WindowsJudged;
                var latest = rig.Tokens.Latest!;
                Assert.False(latest.Verdict == TokenVerdict.TracksStarts && latest.FromUtc != rig.Tokens.Buckets[0].EndUtc,
                    $"{minBuckets} buckets, draw {seed}, minute {i + 1}: a window inside the window says '{latest.Summary}'");
            }
        }
        Assert.True(judged > 10_000, $"only {judged} windows inside the window were judged"); // the test is not empty
    }

    /// <summary>Reading noise: each bucket's reading off its level by up to <paramref name="amp"/> either way (a fixed draw for each <paramref name="seed"/>).</summary>
    private static int[] Noise(int buckets, int amp, int seed)
    {
        var e = new int[buckets];
        uint x = (uint)(seed * 2246822519u + 777);
        for (int i = 0; i < buckets; i++) { x = x * 1664525u + 1013904223u; e[i] = (int)(x >> 8) % (2 * amp + 1) - amp; }
        return e;
    }

    /// <summary>
    /// After <paramref name="quiet"/> buckets of Toke changes, the leak of about 37 a minute read with <paramref name="noise"/>
    /// and other token churn of <paramref name="churn"/> in each bucket: the leak bucket at which the verdict first says Toke
    /// tracks the starts, and the one at which the whole window alone first says so (-1: not within 300). At every bucket
    /// close the verdict says so whenever the whole window alone does.
    /// </summary>
    private static (int verdict, int whole) NoisyLeak(Rig rig, int[] quiet, int[] noise, int[] churn)
    {
        rig.Bucket(0, 0);
        for (int i = 0; i < quiet.Length; i++) rig.Bucket(0, quiet[i], other: OtherStarts[i % OtherStarts.Length]);
        int verdict = -1, whole = -1, last = 0;
        for (int i = 0; i < 300 && (verdict < 0 || whole < 0); i++)
        {
            int start = PerMinute37[i % PerMinute37.Length];
            rig.Bucket(start, start + noise[i] - last + churn[i], other: 3);
            last = noise[i];
            bool tracks = rig.Tokens.Latest!.Verdict == TokenVerdict.TracksStarts, wholeTracks = rig.Tokens.Analyse().Verdict == TokenVerdict.TracksStarts;
            Assert.False(wholeTracks && !tracks, $"leak bucket {i + 1}: the whole window says Toke tracks the starts, the verdict '{rig.Tokens.Latest.Summary}'");
            if (verdict < 0 && tracks) verdict = i + 1;
            if (whole < 0 && wholeTracks) whole = i + 1;
        }
        return (verdict, whole);
    }

    [Theory]
    [InlineData("readings off by up to 25", 14, 38, 23, 38, 14, 16, 7)]
    [InlineData("readings off by up to 50", 33, 83, 33, 89, 14, 74, 1)]
    [InlineData("readings off by up to 25, an earlier rise straight before the leak", 16, 52, 33, 56, 14, 16, 12)]
    [InlineData("readings off by up to 50, an earlier rise straight before the leak", 45, 148, 57, 148, 14, 74, 2)]
    [InlineData("readings off by up to 25, a dip of 100 that comes back just before the leak", 16, 44, 32, 44, 14, 16, 9)]
    [InlineData("other churn of up to 40", 13, 42, 27, 42, 10, 19, 6)]
    public void ANoisyLeakIsWarnedOfNoLaterThanTheWholeWindowAloneWouldWarnOfIt(string model, int soonest, int latest, int wholeSoonest,
        int wholeLatest, int freshSoonest, int freshLatest, int sooner)
    {
        // The tests' noise model, at the default settings, twelve draws: 45 quiet buckets whose readings wander (by up to 25
        // either way, or by up to the reading noise), then the leak of about 37 a minute. Its readings are off by up to 25 or
        // 50 tokens either way, or other token churn of up to 40 either way comes and goes with it; in two of the models an
        // earlier rise of 6 x 120 (services at sign-in, say) comes straight before it, and in one the quiet readings drop by
        // 100 at the 41st quiet bucket and come back at the 45th, the last before the leak. Its own buckets then line up with
        // the starts less well, and the bound a window inside the window must clear, which keeps chance out (the test
        // above), holds it back. The verdict is never later than the whole window alone, at any bucket close (NoisyLeak
        // checks each), and these are the numbers DESIGN.md and TokenCorrelator.Judge state, for these models only: the leak
        // bucket of the first warning (soonest and latest of the draws), of the whole window alone, and of a fresh start as
        // the leak begins (which judges one window at the tracking bar), and in how many draws the leak's own window brought
        // the warning sooner than the whole window alone. An earlier rise holds the whole window back: with readings off by
        // up to 25, in 8 of the 12 draws until the 53rd to 56th leak bucket, as that rise leaves the window.
        bool riseBefore = model.Contains("earlier rise"), churned = model.StartsWith("other churn"), dip = model.Contains("a dip");
        int amp = model.Contains("50") ? 50 : 25;
        var verdicts = new List<int>();
        var wholes = new List<int>();
        var freshes = new List<int>();
        int soonerDraws = 0;
        for (int seed = 1; seed <= 12; seed++)
        {
            int[] noise = churned ? new int[300] : Noise(300, amp, seed);
            int[] churn = churned ? Noise(300, 40, seed + 100) : new int[300];
            int[] quiet = Wander(45, amp, seed);
            if (riseBefore) quiet = [.. quiet, .. Rise(6, 120)];
            if (dip) { quiet[40] -= 100; quiet[44] += 100; } // down at the 41st quiet bucket, back at the 45th, the last
            var (verdict, whole) = NoisyLeak(new Rig(new Settings(), 60), quiet, noise, churn);
            Assert.True(verdict >= 1 && whole >= 1 && verdict <= whole, $"{model}, draw {seed}: verdict at {verdict}, whole window alone at {whole}");
            verdicts.Add(verdict);
            wholes.Add(whole);
            freshes.Add(NoisyLeak(new Rig(new Settings(), 60), [], noise, churn).whole);
            if (verdict < whole) soonerDraws++;
        }
        Assert.Equal((soonest, latest), (verdicts.Min(), verdicts.Max()));
        Assert.Equal((wholeSoonest, wholeLatest), (wholes.Min(), wholes.Max()));
        Assert.Equal((freshSoonest, freshLatest), (freshes.Min(), freshes.Max()));
        Assert.Equal(sooner, soonerDraws);
        // The earlier rise holds the whole window back until it leaves the window: with readings off by up to 25, in 8 of the
        // 12 draws to the 53rd to 56th leak bucket (DESIGN.md states it).
        if (riseBefore && amp == 25)
            Assert.Equal(8, wholes.Count(w => w is >= 53 and <= 56));
    }

    [Fact]
    public void ALeakWhoseOwnWindowNeedsMoreBucketsThanTheNewestIsStillWarnedOf()
    {
        // The test rig's fast leak, its readings off by up to 8 tokens either way, after a long quiet spell. Its newest
        // windows, ten or eleven buckets that begin inside the leak, fall short of the bound a window inside the window must
        // clear there (a correlation of 0.9559 over ten, 0.9487 over eleven). A longer one, older than those, clears it at
        // the leak's 29th bucket (27 buckets, r = 0.88), so every window up to the limit is judged, not only the newest.
        int[] full = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        int[] wobble = [4, -3, 6, -4, 3, -6, 8, -2, -4, 6, -3, 2];
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 120; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        int at = -1;
        for (int i = 0; i < 100 && at < 0; i++)
        {
            rig.Bucket(full[i % 12], full[i % 12] + wobble[i % 12] - (i == 0 ? 0 : wobble[(i - 1) % 12]), other: 3);
            if (Warned(rig)) at = i + 1;
        }
        var f = Assert.Single(rig.Findings);
        Assert.True(at >= 1 && at <= 30, $"warned at leak bucket {at}");
        Assert.True((int)f.Data["buckets"]! > 10, $"warned from a window of {f.Data["buckets"]} buckets");
        Assert.Equal("Codex", f.Image);
    }

    [Fact]
    public void OfARunOfEqualReadingsOnlyTheNewestTwoAreJudgedFrom()
    {
        // Forty buckets of a flat quiet spell, then a rise of 20 a bucket in the test rig (a verdict needs ten buckets and a
        // rise of 100). Every bucket of the spell is at or below every later reading; only its newest two start a window,
        // with the rise's own buckets that have a verdict's worth after them.
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 40; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        for (int i = 0; i < 12; i++) rig.Bucket(0, 20, other: 3);
        Assert.Equal([42, 41, 40, 39], rig.Tokens.RiseStarts());
    }

    [Fact]
    public void AtMostThirtyTwoWindowsInsideTheWindowAreJudgedAtABucketClose()
    {
        // A day's window of one-minute buckets (the Advanced settings allow it): a day of equal readings, then a steady rise
        // that does not follow the starts, so every bucket of the rise is at or below every later reading. However many
        // such buckets there are, at most 32 windows inside the window are judged at a bucket close, the newest first.
        var settings = new Settings { TokenWindowMinutes = 24 * 60 };
        settings.Normalize();
        var rig = new Rig(settings, 60);
        rig.Bucket(0, 0);
        for (int i = 0; i < 24 * 60; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        int most = 0;
        for (int i = 0; i < 200; i++)
        {
            rig.Bucket(OtherStarts[i % OtherStarts.Length], 40);
            Assert.InRange(rig.Tokens.WindowsJudged, 0, TokenCorrelator.MaxRiseWindows);
            most = Math.Max(most, rig.Tokens.WindowsJudged);
        }
        Assert.Equal(TokenCorrelator.MaxRiseWindows, most); // the bound was reached, not merely respected
        Assert.Equal(TokenCorrelator.MaxRiseWindows, rig.Tokens.RiseStarts().Count);
        Assert.Equal(32, TokenCorrelator.MaxRiseWindows);
    }

    [Fact]
    public void ABucketAboveALaterReadingIsNotJudgedFrom()
    {
        // Forty quiet buckets (1 to 40), then bucket 41 holds 50 more tokens and bucket 42 gives them back, then a rise of 20
        // a bucket from bucket 43 in the test rig. Bucket 41 is above the reading after it: a rise from there is judged from
        // that lower reading, so it starts no window of its own. Of the equal readings, the newest two start a window: bucket
        // 42, which gave the 50 back, and bucket 40, the last quiet one; buckets 43 and 44 of the rise start the other two.
        var rig = new Rig();
        rig.Bucket(0, 0);
        for (int i = 0; i < 40; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        rig.Bucket(0, 50, other: 3);
        rig.Bucket(0, -50, other: 3);
        for (int i = 0; i < 12; i++) rig.Bucket(0, 20, other: 3);
        Assert.Equal([44, 43, 42, 40], rig.Tokens.RiseStarts());
    }

    [Fact]
    public void AWindowInsideTheWindowHoldsTenBucketsWhateverAVerdictNeeds()
    {
        // The test rig with four buckets per verdict, the fewest the Advanced settings allow. A fresh start warns of the fast
        // leak at its 7th bucket (its first with a rise of 100). After a quiet spell, the leak's own window must hold ten
        // buckets, as at the default, since over fewer the bound lets chance through (the eight-hour runs above): after 120
        // quiet buckets, or 10, warned of at its 10th bucket, from a window of ten. After fewer than 10 the whole window,
        // judged first at the tracking bar, can warn sooner: after 4, at the 7th (from 11 buckets); after 9, at the 9th.
        var settings = new Settings { TokenBucketSeconds = 10, TokenWindowMinutes = 30, TokenMinBuckets = 4, TokenMinOutstandingRise = 100 };
        settings.Normalize();
        Assert.Equal(4, settings.TokenMinBuckets);
        Assert.Equal(10, TokenCorrelator.MinRiseWindowBuckets);
        int[] full = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        Assert.Equal(7, WarnedAt(new Rig(settings, 10), [], full));
        var rig = new Rig(settings, 10);
        Assert.Equal(10, WarnedAt(rig, Quiet(120), full));
        Assert.Equal(10, (int)Assert.Single(rig.Findings).Data["buckets"]!);
        Assert.Equal("Codex", rig.Tokens.Latest!.Owners[0].Name);
        foreach (var (quiet, at, buckets) in new[] { (10, 10, 10), (4, 7, 11), (9, 9, 18) })
        {
            var after = new Rig(settings, 10);
            Assert.Equal(at, WarnedAt(after, Quiet(quiet), full));
            Assert.Equal(buckets, (int)Assert.Single(after.Findings).Data["buckets"]!);
        }
        // Forty quiet buckets, then twelve of a rise of 20: only buckets with ten or more after them start a window.
        var flat = new Rig(settings, 10);
        flat.Bucket(0, 0);
        for (int i = 0; i < 40; i++) flat.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
        for (int i = 0; i < 12; i++) flat.Bucket(0, 20, other: 3);
        Assert.Equal([42, 41, 40, 39], flat.Tokens.RiseStarts());
    }

    [Theory]
    [InlineData(10, 0.9559, 0.9558)]
    [InlineData(14, 0.9292, 0.9291)]
    [InlineData(27, 0.8723, 0.8722)]
    public void AWindowInsideTheWindowMustClearTheOneSided999PercentBound(int buckets, double clears, double fallsShort)
    {
        // The correlation a window inside the window needs, as TokenCorrelator.Judge and DESIGN.md state it: Fisher's z with
        // z = 3.09 (99.9%, one-sided). A 99% or 97.5% bound (z = 2.326 or 1.96) would let through about 0.92 or 0.90 over ten
        // buckets, and raised false warnings in more eight-hour runs than the whole window alone.
        Assert.True(TokenCorrelator.SpearmanLowerBound(clears, buckets) >= 0.6, $"r = {clears} over {buckets}");
        Assert.True(TokenCorrelator.SpearmanLowerBound(fallsShort, buckets) < 0.6, $"r = {fallsShort} over {buckets}");
        Assert.Equal(-1, TokenCorrelator.SpearmanLowerBound(double.NaN, buckets));
    }

    [Theory]
    [InlineData(-1)] // the wall clock set back an hour
    [InlineData(24)] // and forward a day
    public void AWarningGivesItsWindowsLengthOnTheClockThatOnlyGoesForward(int hours)
    {
        // The wall clock is changed at the leak's fifth bucket, before the warning. The warning still says "+145 in 1 min"
        // (ten ten-second buckets); from the samples' wall-clock times it said "in -3500 s" or "in 24 h 1 min".
        int[] full = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        string Title(TimeSpan shift)
        {
            var rig = new Rig();
            rig.Bucket(0, 0);
            for (int i = 0; i < 120; i++) rig.Bucket(0, 0, other: OtherStarts[i % OtherStarts.Length]);
            for (int i = 0; i < 60 && !Warned(rig); i++)
            {
                if (i == 4) rig.WallShift = shift;
                rig.Bucket(full[i % 12], full[i % 12], other: 3);
            }
            return Assert.Single(rig.Findings, f => (string?)f.Data["verdict"] == "TracksStarts").Title;
        }
        string steady = Title(TimeSpan.Zero);
        Assert.Contains("+145 in 1 min over 175 starts", steady);
        Assert.Equal(steady, Title(TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(1, 5, 1)]    // a one-token dip early in the quiet spell, back the next bucket
    [InlineData(30, 10, 10)] // an app frees 30 tokens; another takes 30 ten buckets later
    [InlineData(30, 100, 10)] // the same, late in the quiet spell
    [InlineData(30, 10, 0)]  // 30 tokens freed for good
    public void ASlowLeakAfterADipIsWarnedOfWithinTheBudgetOfAFreshStart(int depth, int at, int back)
    {
        // The test rig (ten-second buckets, a 30-minute window, a rise of 100) and the slow leak: a fresh start warns of it at
        // its 15th bucket. After 120 quiet buckets with a dip, judged from the dip's low (the lowest reading since the window
        // began), the first three were warned of at buckets 67, 81 and 24.
        int[] half = [0, 10, 2, 20, 0, 15, 5, 0, 12, 7, 17, 0];
        int fresh = WarnedAt(new Rig(), [], half);
        Assert.Equal(15, fresh);
        int warned = WarnedAt(new Rig(), Dip(120, depth, at, back), half);
        Assert.True(warned >= 1 && warned <= fresh, $"dip {depth} at {at}, back {back}: warned at leak bucket {warned}, budget {fresh}");
    }

    [Fact]
    public void AFastLeakIsWarnedOfWithinTheBudgetOfAFreshStartAfterADipOrAnEarlierRise()
    {
        // A fast leak needs only the ten buckets a verdict needs: a fresh start warns of it at its tenth bucket, in the test
        // rig and at the default settings (about 75 tokens a minute). The quiet bucket before its first rise is judged, as
        // the fresh start's first one is, also when that rise begins with a bucket of no change.
        int[] full = [0, 20, 5, 40, 0, 30, 10, 0, 25, 15, 35, 0];
        Assert.Equal(10, WarnedAt(new Rig(), [], full));
        foreach (var before in new[] { Quiet(120), Dip(120, 1, 5, 1), Dip(120, 100, 10, 10), [.. Rise(5, 200), .. Quiet(20)] })
            Assert.InRange(WarnedAt(new Rig(), before, full), 1, 10);
        int[] perMinute75 = PerMinute37.Select(x => x * 2).ToArray();
        Assert.Equal(10, WarnedAt(new Rig(new Settings(), 60), [], perMinute75));
        foreach (var before in new[] { Quiet(45), Uneven(45), Dip(45, 100, 35, 5), [.. Rise(5, 200), .. Quiet(20)] })
            Assert.InRange(WarnedAt(new Rig(new Settings(), 60), before, perMinute75), 1, 10);
    }

    [Fact]
    public void ALeakAfterADipIsWarnedOfWithinTheBudgetOfAFreshStartWithALongerWindowOrASlowerLeak()
    {
        // A three-hour window: 150 quiet minutes with a dip, then the leak of about 37 a minute (fresh start: its 15th
        // bucket; judged from the low of a one-token dip, its 37th). And at the default settings, a leak of about 18 a
        // minute, which a fresh start warns of at its 28th bucket.
        var three = new Settings { TokenWindowMinutes = 180 };
        Assert.Equal(15, WarnedAt(new Rig(three, 60), [], PerMinute37));
        foreach (var before in new[] { Quiet(150), Dip(150, 1, 5, 1), Dip(150, 100, 10, 10), Uneven(150) })
            Assert.InRange(WarnedAt(new Rig(three, 60), before, PerMinute37), 1, 15);
        int[] perMinute18 = PerMinute37.Select(x => x / 2).ToArray();
        int fresh = WarnedAt(new Rig(new Settings(), 60), [], perMinute18);
        Assert.Equal(28, fresh);
        foreach (var before in new[] { Quiet(30), Dip(30, 1, 1, 1), Dip(30, 50, 10, 0) })
            Assert.InRange(WarnedAt(new Rig(new Settings(), 60), before, perMinute18), 1, fresh);
    }

    [Fact]
    public void QuietThenBurstEpisodesOfLongLivedAppsWithNoLeakRaiseNoWarning()
    {
        // Eight hours at the default settings (a minute a bucket, a one-hour window, ten buckets, a rise of 500). Again and
        // again: a quiet spell in which short-lived processes start and end (Toke wanders a little), then apps are opened (30
        // to 40 starts in three minutes), which hold their tokens and then take more while they run, 600 to 900 in all, not
        // with the starts, until they are closed and free them all. Each episode's rise is judged from where it began (the
        // rule runs), and none of those windows says Toke tracks the starts, so nothing is reported as a leak.
        var rig = new Rig(new Settings(), 60);
        int judged = 0, buckets = 0;
        void Step(int starts, int tokens, int other)
        {
            rig.Bucket(starts, tokens, other);
            buckets++;
            if (rig.Tokens.RiseStarts().Count > 0) judged++;
        }
        rig.Bucket(0, 0);
        int[] quiet = [45, 70, 30, 55, 80, 40];
        int[] burst = [30, 40, 34, 38, 32, 36];
        int[] grow = [600, 900, 700, 800, 650, 850];
        for (int e = 0; e < quiet.Length; e++)
        {
            for (int i = 0; i < quiet[e]; i++) Step(0, (i * 7 + e) % 31 - 15, OtherStarts[i % OtherStarts.Length]);
            int each = burst[e] / 3;
            for (int i = 0; i < 3; i++) Step(each, each, 2);                               // the apps start, a token each
            for (int i = 0; i < 20; i++) Step(i % 7 == 0 ? 1 : 0, grow[e] / 20, i % 3);    // and take more while they run
            for (int i = 0; i < 4; i++) Step(0, -(burst[e] + grow[e]) / 4, 1);             // closed: all of it freed
        }
        Assert.True(buckets > 450, $"{buckets} buckets");
        Assert.True(judged > 0, "no rise was judged from where it began, so the rule never ran");
        Assert.False(Warned(rig));
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
