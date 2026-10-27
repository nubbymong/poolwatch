using System.Text.RegularExpressions;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// The main window's status line and its small formatters (kept in Core so the wording is unit-tested on every OS).
/// </summary>
public class StatusLineTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static PoolStatus Describe(long? total, TimeSpan held, long change10, long? changeHour, PoolStatusLead? lead = null) =>
        PoolStatusLine.Describe(total, held, change10, changeHour, lead);

    [Fact]
    public void ASteadyPoolSaysSoWithTheTotal()
    {
        var s = Describe((long)(9.6 * GB), TwoHours, 20 * MB, -30 * MB);
        Assert.Equal(PoolStatusKind.Steady, s.Kind);
        Assert.Equal("Kernel pool is steady (9.6 GB).", s.Text);
    }

    [Fact]
    public void AGrowingPoolNamesTheMostLikelyCauseWithItsConfidenceInWords()
    {
        var lead = new PoolStatusLead("File objects (File)", (long)(1.1 * GB), TimeSpan.FromMinutes(50), "Stream Deck", Confidence.StronglyCorrelated);
        var s = Describe(10 * GB, TwoHours, 150 * MB, (long)(1.2 * GB), lead);
        Assert.Equal(PoolStatusKind.Growing, s.Kind);
        Assert.Equal("Kernel pool is growing: +1.2 GB in the last hour. Most likely cause: Stream Deck (strong evidence).", s.Text);

        var traced = Describe(10 * GB, TwoHours, 150 * MB, (long)(1.2 * GB), lead with { Confidence = Confidence.ConfirmedByTrace });
        Assert.EndsWith("Most likely cause: Stream Deck (confirmed by a trace).", traced.Text);
    }

    [Fact]
    public void AGrowingPoolWithoutAConvincingSuspectSaysNoClearCauseYet()
    {
        // No lead at all.
        var none = Describe(10 * GB, TwoHours, 20 * MB, 300 * MB);
        Assert.Equal(PoolStatusKind.Growing, none.Kind);
        Assert.Equal("Kernel pool is growing: +300 MB in the last hour. No clear cause yet.", none.Text);

        // A lead whose suspect has only weak evidence is not named as the cause; what grows most is still said.
        var weak = new PoolStatusLead("ReFS per-file control blocks (Reff, refs.sys)", 280 * MB, TimeSpan.FromHours(1), "Explorer", Confidence.Weak);
        var s = Describe(10 * GB, TwoHours, 20 * MB, 300 * MB, weak);
        Assert.Equal("Kernel pool is growing: +300 MB in the last hour. No clear cause yet (growing most: ReFS per-file control blocks (Reff, refs.sys)).", s.Text);
        Assert.DoesNotContain("Explorer", s.Text);
    }

    [Fact]
    public void AFastRiseIsReportedOverTheLastTenMinutesBeforeAnHourIsHeld()
    {
        var s = Describe(10 * GB, TimeSpan.FromMinutes(25), 400 * MB, changeHour: null);
        Assert.Equal(PoolStatusKind.Growing, s.Kind);
        Assert.StartsWith("Kernel pool is growing: +400 MB in the last 10 minutes.", s.Text);

        // Seven minutes held: the span says so.
        Assert.StartsWith("Kernel pool is growing: +150 MB in the last 7 minutes.", Describe(10 * GB, TimeSpan.FromMinutes(7), 150 * MB, null).Text);
    }

    [Fact]
    public void ADroppingPoolSaysHowMuchItDroppedAndNeverPrintsANegativeRise()
    {
        var s = Describe((long)(9.4 * GB), TwoHours, -150 * MB, -100 * MB);
        Assert.Equal(PoolStatusKind.Dropping, s.Kind);
        Assert.Equal("Kernel pool dropped 150 MB in the last 10 minutes.", s.Text);

        // The old Overview could read "Rising: -148.2 MB": a fall over the hour is a drop, in positive words.
        var hour = Describe(9 * GB, TwoHours, -10 * MB, -600 * MB);
        Assert.Equal(PoolStatusKind.Dropping, hour.Kind);
        Assert.Equal("Kernel pool dropped 600 MB in the last hour.", hour.Text);

        // A clear just now outranks the hour's growth: the current news is the drop.
        Assert.Equal(PoolStatusKind.Dropping, Describe(9 * GB, TwoHours, -2 * GB, 1 * GB).Kind);
    }

    [Fact]
    public void TooFewSamplesSayThereIsNotEnoughDataYet()
    {
        var first = Describe(null, TimeSpan.Zero, 0, null);
        Assert.Equal(PoolStatusKind.Collecting, first.Kind);
        Assert.Equal("Kernel pool: collecting the first sample…", first.Text);

        // A minute of samples: too early to judge the total, even with a big change.
        var early = Describe((long)(9.6 * GB), TimeSpan.FromMinutes(1), 500 * MB, null);
        Assert.Equal(PoolStatusKind.Collecting, early.Kind);
        Assert.Equal("Kernel pool is 9.6 GB. Not enough data yet to tell whether it is growing (PoolWatch needs a few minutes of samples).", early.Text);

        // ... but a growth the detector already caught (an open incident) is said, with its cause.
        var caught = Describe((long)(9.6 * GB), TimeSpan.FromMinutes(1), 500 * MB, null,
            new PoolStatusLead("File objects (File)", 12 * MB, TimeSpan.FromSeconds(40), "Leaker.exe (7716)", Confidence.StronglyCorrelated));
        Assert.Equal(PoolStatusKind.TagGrowing, caught.Kind);
        Assert.Equal("Kernel pool is 9.6 GB; File objects (File) grew +12 MB in 40 s. Most likely cause: Leaker.exe (7716) (strong evidence).", caught.Text);
    }

    [Fact]
    public void ATagGrowingWhileTheTotalHoldsIsNamed()
    {
        // An open incident on one tag while other tags shrink: the total is steady, the line still says what grows and why.
        var lead = new PoolStatusLead("File objects (File)", 400 * MB, TimeSpan.FromMinutes(25), "Leaker.exe (7716)", Confidence.Correlated);
        var s = Describe((long)(9.6 * GB), TwoHours, -50 * MB, -150 * MB, lead);
        Assert.Equal(PoolStatusKind.TagGrowing, s.Kind);
        Assert.Equal("Kernel pool is steady (9.6 GB), but File objects (File) grew +400 MB in 25 min. Most likely cause: Leaker.exe (7716) (some evidence).", s.Text);

        // A lead that did not grow (a release incident, a tag back down) is never named.
        var shrunk = Describe((long)(9.6 * GB), TwoHours, 0, 0, lead with { Growth = -300 * MB });
        Assert.Equal("Kernel pool is steady (9.6 GB).", shrunk.Text);
    }

    [Fact]
    public void NoInputEverPrintsANegativeAmount()
    {
        var negative = new Regex(@"(\+-|-\d|−)");
        long[] changes = [-3 * GB, -400 * MB, -150 * MB, -99 * MB, -1, 0, 1, 99 * MB, 150 * MB, 400 * MB, 3 * GB];
        var leads = new PoolStatusLead?[]
        {
            null,
            new("File objects (File)", 300 * MB, TimeSpan.FromMinutes(30), "app.exe", Confidence.StronglyCorrelated),
            new("File objects (File)", -300 * MB, TimeSpan.FromMinutes(30), "app.exe", Confidence.Weak),
        };
        foreach (var held in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TwoHours })
        foreach (var c10 in changes)
        foreach (var c60 in changes.Select(c => (long?)c).Append(null))
        foreach (var lead in leads)
        {
            var s = Describe(9 * GB, held, c10, c60, lead);
            Assert.False(negative.IsMatch(s.Text), $"negative amount in: {s.Text}");
            Assert.DoesNotContain("Rising", s.Text);
        }
    }

    [Fact]
    public void ConfidenceIsInWords()
    {
        Assert.Equal("confirmed by a trace", PlainWords.Confidence(Confidence.ConfirmedByTrace));
        Assert.Equal("strong evidence", PlainWords.Confidence(Confidence.StronglyCorrelated));
        Assert.Equal("some evidence", PlainWords.Confidence(Confidence.Correlated));
        Assert.Equal("weak evidence", PlainWords.Confidence(Confidence.Weak));
    }

    [Fact]
    public void WhenReadsAsAClockTodayAndADateOtherwise()
    {
        var now = new DateTime(2026, 10, 27, 15, 30, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("14:02", PlainWords.When(new DateTime(2026, 10, 27, 14, 2, 0, DateTimeKind.Local).ToUniversalTime(), now));
        Assert.Equal("yesterday 23:10", PlainWords.When(new DateTime(2026, 10, 26, 23, 10, 0, DateTimeKind.Local).ToUniversalTime(), now));
        Assert.Equal("24 Oct 09:05", PlainWords.When(new DateTime(2026, 10, 24, 9, 5, 0, DateTimeKind.Local).ToUniversalTime(), now));
    }

    [Fact]
    public void RatesArePerHourAndUnsigned()
    {
        Assert.Equal("600 MB/h", PlainWords.PerHour(100 * MB, TimeSpan.FromMinutes(10)));
        Assert.Equal("600 MB/h", PlainWords.PerHour(-100 * MB, TimeSpan.FromMinutes(10)));
        Assert.Equal("1.2 GB/h", PlainWords.PerHour((long)(1.2 * GB), TimeSpan.FromHours(1)));
        Assert.Equal("60 MB/h", PlainWords.PerHour(1 * MB, TimeSpan.FromSeconds(5))); // under a minute counts as one minute
    }

    [Fact]
    public void ChartStepsAreRound()
    {
        Assert.Equal((long)(2.5 * GB), ChartScale.Step((long)(9.6 * GB)));
        Assert.Equal(10 * GB, ChartScale.Top((long)(9.6 * GB), ChartScale.Step((long)(9.6 * GB))));
        Assert.Equal(2 * GB, ChartScale.Step(6 * GB));
        Assert.Equal(8 * GB, ChartScale.Top(6 * GB, 2 * GB));
        Assert.Equal(100 * MB, ChartScale.Step(300 * MB));
        Assert.Equal(1 * GB, ChartScale.Step(2100 * MB)); // over 500 MB a step: a whole gigabyte, never "1000 MB"
        Assert.Equal(1 * MB, ChartScale.Step(0));
        Assert.Equal(1 * MB, ChartScale.Step(100 * 1024)); // never below a megabyte

        Assert.Equal("0", ChartScale.Label(0, 2 * GB));
        Assert.Equal("2.5 GB", ChartScale.Label((long)(2.5 * GB), (long)(2.5 * GB)));
        Assert.Equal("10 GB", ChartScale.Label(10 * GB, (long)(2.5 * GB)));
        Assert.Equal("300 MB", ChartScale.Label(300 * MB, 100 * MB));
    }
}
