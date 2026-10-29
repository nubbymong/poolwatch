using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// The main window's status line and its small formatters (kept in Core so the wording is unit-tested on every OS).
/// The line is for someone who is not a kernel expert: what grew or shrank, by how much and over what time, in rounded
/// amounts without a sign; everyday tag names, one pair of brackets at most.
/// </summary>
public class StatusLineTests
{
    private const long KB = 1L << 10, MB = 1L << 20, GB = 1L << 30;
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);
    private const string Metadata = "File system metadata (Reff, refs.sys)";
    private const string OpenFiles = "Open files (File, Windows kernel)";

    private static PoolStatus Describe(long? total, TimeSpan held, long change10, long? changeHour, PoolStatusLead? lead = null, string? saturated = null,
        string? ownClear = null, string? nextClear = null) =>
        PoolStatusLine.Describe(total, held, change10, changeHour, lead, saturated, ownClear, nextClear);

    /// <summary>Brackets inside brackets ("(growing most: X (Reff))"): the wording must never do that.</summary>
    private static readonly Regex Nested = new(@"\([^()]*\(");

    [Fact]
    public void ASteadyPoolSaysSoWithTheTotal()
    {
        var s = Describe((long)(9.6 * GB), TwoHours, 20 * MB, -30 * MB);
        Assert.Equal(PoolStatusKind.Steady, s.Kind);
        Assert.Equal("Kernel pool is steady at 9.6 GB.", s.Text);
    }

    [Fact]
    public void AGrowingPoolSaysWhatGrewByHowMuchAndOverWhatTime()
    {
        // The hour figure is the committed pool now minus the committed pool an hour ago. It used to read
        // "Kernel pool is growing: +708.5 MB in the last hour."
        var s = Describe(10 * GB, TwoHours, 20 * MB, (long)(708.5 * MB));
        Assert.Equal(PoolStatusKind.Growing, s.Kind);
        Assert.Equal("Kernel pool grew 709 MB in the last hour.", s.Text);
        Assert.StartsWith("Kernel pool grew 1.4 GB in the last hour.", Describe(10 * GB, TwoHours, 20 * MB, (long)(1.37 * GB)).Text);
    }

    [Fact]
    public void AGrowingPoolNamesACauseInSoftWordsOnlyWithStrongEvidence()
    {
        var lead = new PoolStatusLead(OpenFiles, (long)(1.1 * GB), TimeSpan.FromMinutes(50), "Stream Deck", Confidence.StronglyCorrelated);
        var s = Describe(10 * GB, TwoHours, 150 * MB, (long)(1.2 * GB), lead);
        Assert.Equal(PoolStatusKind.Growing, s.Kind);
        Assert.Equal("Kernel pool grew 1.2 GB in the last hour. PoolWatch is keeping an eye on Stream Deck.", s.Text);

        var traced = Describe(10 * GB, TwoHours, 150 * MB, (long)(1.2 * GB), lead with { Confidence = Confidence.ConfirmedByTrace, Cause = "Example Device Service" });
        Assert.EndsWith("PoolWatch is keeping an eye on Example Device Service.", traced.Text);
    }

    [Fact]
    public void AGrowingPoolWithoutAStrongSuspectNamesNoCause()
    {
        // No lead at all.
        var none = Describe(10 * GB, TwoHours, 20 * MB, 300 * MB);
        Assert.Equal(PoolStatusKind.Growing, none.Kind);
        Assert.Equal("Kernel pool grew 300 MB in the last hour.", none.Text);

        // A suspect with weak or only some evidence is not named (growth often goes away by itself; Pool causes has the details).
        foreach (var confidence in new[] { Confidence.Weak, Confidence.Correlated })
        {
            var s = Describe(10 * GB, TwoHours, 20 * MB, 300 * MB, new PoolStatusLead(Metadata, 280 * MB, TimeSpan.FromHours(1), "Explorer", confidence));
            Assert.Equal("Kernel pool grew 300 MB in the last hour.", s.Text);
        }
    }

    [Fact]
    public void PoolWatchsOwnClearIsSaidAsItsResultAndAGrowthSaysWhenTheNextOneRuns()
    {
        const string clear = "The cache clear at 03:56 freed 1.3 GB.", next = "The next cache clear is in 12 min.";
        // The changes are measured from the level the clear left, so the pool is steady and the drop is the clear's result.
        var after = Describe((long)(6.2 * GB), TwoHours, 10 * MB, 20 * MB, ownClear: clear, nextClear: next);
        Assert.Equal(PoolStatusKind.Steady, after.Kind);
        Assert.Equal("Kernel pool is steady at 6.2 GB. The cache clear at 03:56 freed 1.3 GB.", after.Text);

        // A growth says when the next clear runs, after a cause and after the last clear's result.
        Assert.Equal("Kernel pool grew 484 MB in the last hour. The next cache clear is in 12 min.", Describe(10 * GB, TwoHours, 20 * MB, 484 * MB, nextClear: next).Text);
        var lead = new PoolStatusLead(Metadata, 400 * MB, TimeSpan.FromHours(1), "Explorer", Confidence.StronglyCorrelated);
        Assert.Equal("Kernel pool is steady at 9.6 GB, but File system metadata (Reff, refs.sys) grew 400 MB in the last hour. PoolWatch is keeping an eye on " +
                     "Explorer. The cache clear at 03:56 freed 1.3 GB. The next cache clear is in 12 min.",
            Describe((long)(9.6 * GB), TwoHours, 0, 0, lead, ownClear: clear, nextClear: next).Text);

        // Steady or shrinking: nothing to wait for. The saturation clause stays last.
        Assert.Equal("Kernel pool is steady at 9.6 GB.", Describe((long)(9.6 * GB), TwoHours, 0, 0, nextClear: next).Text);
        Assert.Equal("Kernel pool shrank 600 MB in the last hour.", Describe(9 * GB, TwoHours, -10 * MB, -600 * MB, nextClear: next).Text);
        Assert.EndsWith(clear + " Your PC is saturated: every core is busy.", Describe((long)(6.2 * GB), TwoHours, 0, 0, saturated: "", ownClear: clear).Text);
    }

    [Fact]
    public void AFastRiseIsReportedOverTheLastTenMinutesBeforeAnHourIsHeld()
    {
        var s = Describe(10 * GB, TimeSpan.FromMinutes(25), 400 * MB, changeHour: null);
        Assert.Equal(PoolStatusKind.Growing, s.Kind);
        Assert.StartsWith("Kernel pool grew 400 MB in the last 10 minutes.", s.Text);

        // Seven minutes held: the span says so.
        Assert.StartsWith("Kernel pool grew 150 MB in the last 7 minutes.", Describe(10 * GB, TimeSpan.FromMinutes(7), 150 * MB, null).Text);
    }

    [Fact]
    public void AShrinkingPoolSaysHowMuchItShrankAndNeverPrintsANegativeRise()
    {
        var s = Describe((long)(9.4 * GB), TwoHours, -150 * MB, -100 * MB);
        Assert.Equal(PoolStatusKind.Dropping, s.Kind);
        Assert.Equal("Kernel pool shrank 150 MB in the last 10 minutes.", s.Text);

        // The old Overview could read "Rising: -148.2 MB": a fall over the hour is a drop, in positive words.
        var hour = Describe(9 * GB, TwoHours, -10 * MB, -600 * MB);
        Assert.Equal(PoolStatusKind.Dropping, hour.Kind);
        Assert.Equal("Kernel pool shrank 600 MB in the last hour.", hour.Text);
        Assert.Equal("Kernel pool shrank 2.5 GB in the last 10 minutes.", Describe(7 * GB, TwoHours, (long)(-2.46 * GB), null).Text);

        // A clear just now outranks the hour's growth: the current news is the drop.
        Assert.Equal(PoolStatusKind.Dropping, Describe(9 * GB, TwoHours, -2 * GB, 1 * GB).Kind);
    }

    [Fact]
    public void TooFewSamplesSayItIsTooEarlyToTell()
    {
        var first = Describe(null, TimeSpan.Zero, 0, null);
        Assert.Equal(PoolStatusKind.Collecting, first.Kind);
        Assert.Equal("Kernel pool: collecting the first sample…", first.Text);

        // A minute of samples: too early to judge the total, even with a big change.
        var early = Describe((long)(9.6 * GB), TimeSpan.FromMinutes(1), 500 * MB, null);
        Assert.Equal(PoolStatusKind.Collecting, early.Kind);
        Assert.Equal("Kernel pool is 9.6 GB. It takes a few minutes to tell whether it is growing.", early.Text);

        // ... but a growth the detector already caught (an open incident) is said, with its cause.
        var caught = Describe((long)(9.6 * GB), TimeSpan.FromMinutes(1), 500 * MB, null,
            new PoolStatusLead(OpenFiles, 12 * MB, TimeSpan.FromSeconds(40), "Leaker", Confidence.StronglyCorrelated));
        Assert.Equal(PoolStatusKind.TagGrowing, caught.Kind);
        Assert.Equal("Kernel pool is 9.6 GB, and Open files (File, Windows kernel) grew 12 MB in 40 seconds. PoolWatch is keeping an eye on Leaker.", caught.Text);
    }

    [Fact]
    public void ATagGrowingWhileTheTotalHoldsIsNamedInPlainWords()
    {
        // An open incident on one tag while other tags shrink: the total is steady, the line still says what grows and why.
        var lead = new PoolStatusLead(Metadata, 400 * MB, TimeSpan.FromHours(1), "Explorer", Confidence.StronglyCorrelated);
        var s = Describe((long)(9.6 * GB), TwoHours, -50 * MB, -150 * MB, lead);
        Assert.Equal(PoolStatusKind.TagGrowing, s.Kind);
        Assert.Equal("Kernel pool is steady at 9.6 GB, but File system metadata (Reff, refs.sys) grew 400 MB in the last hour. PoolWatch is keeping an eye on Explorer.", s.Text);

        Assert.Contains("grew 400 MB in 25 minutes.", Describe((long)(9.6 * GB), TwoHours, 0, 0, lead with { Over = TimeSpan.FromMinutes(25) }).Text);

        // A lead that did not grow (a release incident, a tag back down) is never named.
        var shrunk = Describe((long)(9.6 * GB), TwoHours, 0, 0, lead with { Growth = -300 * MB });
        Assert.Equal("Kernel pool is steady at 9.6 GB.", shrunk.Text);

        // Nor is one that grew too little to show: a tag that fell after a cache clear and is back to within a few hundred
        // KB of its level an hour ago would read "grew 0 MB". From half a megabyte it rounds to "1 MB" and is said.
        var tiny = lead with { Growth = 300 * KB };
        Assert.Equal("Kernel pool is steady at 9.6 GB.", Describe((long)(9.6 * GB), TwoHours, 0, 0, tiny).Text);
        Assert.Equal("Kernel pool is 9.6 GB. It takes a few minutes to tell whether it is growing.", Describe((long)(9.6 * GB), TimeSpan.FromMinutes(1), 0, null, tiny).Text);
        Assert.Contains("grew 1 MB in the last hour.", Describe((long)(9.6 * GB), TwoHours, 0, 0, lead with { Growth = 512 * KB }).Text);
        // While the total grows, the tag's amount is not said, so the same lead's cause is still named.
        Assert.Equal("Kernel pool grew 300 MB in the last hour. PoolWatch is keeping an eye on Explorer.", Describe(10 * GB, TwoHours, 20 * MB, 300 * MB, tiny).Text);
    }

    [Fact]
    public void ASaturatedPcIsSaidInOneShortClause()
    {
        var s = Describe((long)(9.6 * GB), TwoHours, 0, 0, saturated: "MsMpEng.exe");
        Assert.Equal(PoolStatusKind.Steady, s.Kind);
        Assert.True(s.Saturated);
        Assert.Equal("Kernel pool is steady at 9.6 GB. Your PC is saturated; busiest: MsMpEng.exe.", s.Text);

        Assert.EndsWith(" Your PC is saturated: every core is busy.", Describe((long)(9.6 * GB), TwoHours, 0, 0, saturated: "").Text);
        Assert.False(Describe((long)(9.6 * GB), TwoHours, 0, 0).Saturated);
    }

    [Fact]
    public void NoInputEverPrintsASignAnUnroundedAmountOrNestedBrackets()
    {
        var signed = new Regex(@"[+\-−]\d");
        var unrounded = new Regex(@"\d\.\d+ MB|\d\.\d{2,} GB");
        long[] changes = [-3 * GB, -400 * MB, -150 * MB, -99 * MB, -1, 0, 1, 99 * MB, 150 * MB, (long)(708.5 * MB), 400 * MB, (long)(3.27 * GB)];
        var leads = new PoolStatusLead?[]
        {
            null,
            new(Metadata, 300 * MB, TimeSpan.FromMinutes(30), "app.exe (PID 12)", Confidence.StronglyCorrelated),
            new(OpenFiles, 300 * MB, TimeSpan.FromMinutes(30), "app.exe", Confidence.Weak),
            new(Metadata, -300 * MB, TimeSpan.FromMinutes(30), "app.exe", Confidence.Weak),
            new(Metadata, 300 * KB, TimeSpan.FromMinutes(30), "app.exe (PID 12)", Confidence.StronglyCorrelated),
        };
        foreach (var held in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TwoHours })
        foreach (var c10 in changes)
        foreach (var c60 in changes.Select(c => (long?)c).Append(null))
        foreach (var lead in leads)
        foreach (var saturated in new[] { null, "", "app.exe" })
        {
            var s = Describe((long)(9.63 * GB), held, c10, c60, lead, saturated);
            Assert.False(signed.IsMatch(s.Text), $"a sign in: {s.Text}");
            Assert.False(unrounded.IsMatch(s.Text), $"an unrounded amount in: {s.Text}");
            Assert.False(Nested.IsMatch(s.Text), $"nested brackets in: {s.Text}");
            Assert.DoesNotContain(" 0 MB", s.Text);
            Assert.DoesNotContain("growing:", s.Text);
            Assert.DoesNotContain("Rising", s.Text);
        }
    }

    [Fact]
    public void TheLineChangesOnlyWhenWhatItSaysChanges()
    {
        // A sample every 10 s moves the pool by a few MB, and every new text repaints the line: a move below the shown
        // precision leaves the text exactly as it was ("+708.5 MB" and "9.63 GB" changed with nearly every sample).
        Assert.Equal(Describe((long)(9.60 * GB), TwoHours, 3 * MB, 20 * MB).Text, Describe((long)(9.63 * GB), TwoHours, 5 * MB, 24 * MB).Text);
        Assert.Equal(Describe(10 * GB, TwoHours, 20 * MB, (long)(708.5 * MB)).Text, Describe(10 * GB, TwoHours, 22 * MB, (long)(708.9 * MB)).Text);
        Assert.Equal(Describe(9 * GB, TwoHours, (long)(-150.2 * MB), null).Text, Describe(9 * GB, TwoHours, (long)(-150.4 * MB), null).Text);
        var lead = new PoolStatusLead(Metadata, (long)(400.2 * MB), TimeSpan.FromHours(1), "Explorer", Confidence.Correlated);
        Assert.Equal(Describe((long)(9.6 * GB), TwoHours, 0, 0, lead).Text, Describe((long)(9.62 * GB), TwoHours, 0, 0, lead with { Growth = (long)(400.4 * MB) }).Text);

        // A change the line can show is shown.
        Assert.NotEqual(Describe(10 * GB, TwoHours, 20 * MB, 709 * MB).Text, Describe(10 * GB, TwoHours, 20 * MB, 712 * MB).Text);
    }

    [Fact]
    public void AmountsAreWholeMegabytesBelowAGigabyteAndOneDecimalAbove()
    {
        Assert.Equal("0 MB", PlainWords.Amount(0));
        Assert.Equal("1 MB", PlainWords.Amount(1 * MB));
        Assert.Equal("709 MB", PlainWords.Amount((long)(708.5 * MB)));
        Assert.Equal("1023 MB", PlainWords.Amount((long)(1023.4 * MB)));
        Assert.Equal("1.0 GB", PlainWords.Amount((long)(1023.6 * MB))); // never "1024 MB"
        Assert.Equal("1.3 GB", PlainWords.Amount((long)(1.25 * GB)));
        Assert.Equal("9.6 GB", PlainWords.Amount((long)(9.63 * GB)));
        Assert.Equal("12.3 GB", PlainWords.Amount((long)(12.34 * GB)));
        Assert.Equal("150 MB", PlainWords.Amount(-150 * MB)); // no sign: "shrank 150 MB" says which way
    }

    [Fact]
    public void TagsHaveEverydayNamesWithTheirCodeAndDriver()
    {
        // The built-in table (no Debugging Tools installed): the everyday name, then the tag code and its driver.
        Assert.Equal("File system metadata (Reff, refs.sys)", PlainWords.Tag(PoolTag.Parse("Reff"), BuiltinTags.Table[PoolTag.Parse("Reff")]));
        Assert.Equal("Open files (File, Windows kernel)", PlainWords.Tag(PoolTag.Parse("File"), BuiltinTags.Table[PoolTag.Parse("File")]));
        Assert.Equal("Cached file mappings (MmSt, Windows kernel)", PlainWords.Tag(PoolTag.Parse("MmSt"), BuiltinTags.Table[PoolTag.Parse("MmSt")]));
        // pooltag.txt's own wording does not replace a known name; an unknown owner is left out.
        Assert.Equal("Open files (File)", PlainWords.Tag(PoolTag.Parse("File"), new TagInfo("<unknown>", "File objects", "pooltag.txt")));
        // Every tag in the built-in table reads without jargon codes or brackets inside the name.
        foreach (var (tag, info) in BuiltinTags.Table)
        {
            string words = PlainWords.Tag(tag, info);
            Assert.DoesNotMatch(Nested, words);
            Assert.DoesNotMatch(new Regex(@"[A-Z]+_[A-Z]+"), words);
            Assert.DoesNotContain("nt!", words);
        }
    }

    [Fact]
    public void ATagWithoutPoolTagTxtDataFallsBackPlainly()
    {
        var abcd = PoolTag.Parse("Abcd");
        // Found only by scanning the drivers: no description, just the driver.
        Assert.Equal("Pool tag Abcd (foo.sys)", PlainWords.Tag(abcd, new TagInfo("foo.sys", "", "driver scan")));
        // Nothing known at all.
        Assert.Equal("Pool tag Abcd", PlainWords.Tag(abcd, null));
        Assert.Equal("Pool tag Abcd", PlainWords.Tag(abcd, new TagInfo("<unknown>", "", "none")));
        // A pooltag.txt description that is a short phrase is used without its bracketed aside ...
        Assert.Equal("NetIO buffers (Abcd, netio.sys)", PlainWords.Tag(abcd, new TagInfo("netio.sys", "NetIO buffers (per connection)", "pooltag.txt")));
        // ... but a structure name ("NAME_CACHE_NODE") or a long sentence is not plain words.
        Assert.Equal("Pool tag Abcd (fltmgr.sys)", PlainWords.Tag(abcd, new TagInfo("fltmgr.sys", "NAME_CACHE_NODE structure", "pooltag.txt")));
        Assert.Equal("Pool tag Abcd (foo.sys)", PlainWords.Tag(abcd, new TagInfo("foo.sys", new string('x', 41), "pooltag.txt")));
    }

    [Fact]
    public void ProcessesCarryTheirPidInOnePairOfBrackets()
    {
        Assert.Equal("Leaker.exe (PID 7716)", PlainWords.WithPid("Leaker.exe", 7716));
        Assert.Equal("svchost.exe (Schedule, PID 1234)", PlainWords.WithPid("svchost.exe (Schedule)", 1234));
    }

    [Fact]
    public void SpansReadAsWords()
    {
        Assert.Equal("40 seconds", PlainWords.Span(TimeSpan.FromSeconds(40)));
        Assert.Equal("a minute", PlainWords.Span(TimeSpan.FromSeconds(70)));
        Assert.Equal("25 minutes", PlainWords.Span(TimeSpan.FromMinutes(25)));
        Assert.Equal("the last hour", PlainWords.Span(TimeSpan.FromMinutes(60)));
        Assert.Equal("2 hours 10 minutes", PlainWords.Span(TimeSpan.FromMinutes(130)));
        Assert.Equal("1 hour 30 minutes", PlainWords.Span(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void ConfidenceIsInWords()
    {
        Assert.Equal("confirmed by a trace", PlainWords.Confidence(Confidence.ConfirmedByTrace));
        Assert.Equal("strong evidence", PlainWords.Confidence(Confidence.StronglyCorrelated));
        Assert.Equal("some evidence", PlainWords.Confidence(Confidence.Correlated));
        Assert.Equal("weak evidence", PlainWords.Confidence(Confidence.Weak));
        Assert.Equal("with strong evidence", PlainWords.ConfidencePhrase(Confidence.StronglyCorrelated));
        Assert.Equal("confirmed by a trace", PlainWords.ConfidencePhrase(Confidence.ConfirmedByTrace));
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
        Assert.Equal("7.5 MB", ChartScale.Label((long)(7.5 * MB), (long)(2.5 * MB))); // a small step keeps its half
    }
}
