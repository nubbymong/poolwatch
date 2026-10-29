using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Impact;

namespace PoolWatch.Tests;

/// <summary>
/// The impact scale: growth rates, the known causes from the scan, and which level wins. Every other test reads its
/// boundaries and levels from the rules' own constants; only <see cref="TheScaleIsTheAgreedOne"/> pins the agreed values.
/// </summary>
public class ImpactRulesTests
{
    private static readonly DateTime T = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The smallest hourly rate whose day reaches <paramref name="perDay"/>.</summary>
    private static long FirstHourlyRateReaching(long perDay) => (perDay + ImpactRules.HoursPerDay - 1) / ImpactRules.HoursPerDay;

    /// <summary>
    /// The agreed scale, pinned on purpose: these values are what the app tells people ("High impact"), so retuning one
    /// must show in review. When a constant in <see cref="ImpactRules"/> changes, change it here in the same commit; no
    /// other test, caller or window needs to change.
    /// </summary>
    [Fact]
    public void TheScaleIsTheAgreedOne()
    {
        Assert.Equal(10 * ImpactRules.MB, ImpactRules.NoneBelowPerHour);
        Assert.Equal(5 * ImpactRules.GB, ImpactRules.LowBelowPerDay);
        Assert.Equal(10 * ImpactRules.GB, ImpactRules.MediumBelowPerDay);
        Assert.Equal(ImpactLevel.High, ImpactRules.RefsMetadataLevel);
        Assert.Equal(ImpactLevel.Medium, ImpactRules.DefenderDevDriveLevel);
        Assert.Equal(ImpactLevel.Low, ImpactRules.DefenderNoDevDriveLevel);
        Assert.Equal(ImpactLevel.Low, ImpactRules.CodexShaderCacheLevel);
        Assert.Equal(ImpactLevel.Low, ImpactRules.BackgroundServicesLevel);
        Assert.Equal(ImpactLevel.High, ImpactRules.TokenLeakLevel);
    }

    [Fact]
    public void TheLevelsCompareInOrder()
    {
        Assert.True(ImpactLevel.None < ImpactLevel.Low);
        Assert.True(ImpactLevel.Low < ImpactLevel.Medium);
        Assert.True(ImpactLevel.Medium < ImpactLevel.High);
        Assert.Equal(ImpactLevel.High, new[] { ImpactLevel.Low, ImpactLevel.High, ImpactLevel.None }.Max());
    }

    [Fact]
    public void TheUnitsAreTheOnesTheAppShows()
    {
        Assert.Equal(1L << 20, ImpactRules.MB);
        Assert.Equal(1L << 30, ImpactRules.GB);
        Assert.Equal("1.0 GB", Fmt.Bytes(ImpactRules.GB)); // what the app shows for one of these gigabytes
        Assert.Equal("10 MB", Fmt.Bytes(10 * ImpactRules.MB));
    }

    [Fact]
    public void TheThresholdsStayInOrderSoEveryLevelCanBeReached()
    {
        Assert.True(ImpactRules.NoneBelowPerHour > 0);
        Assert.True(FirstHourlyRateReaching(ImpactRules.LowBelowPerDay) > ImpactRules.NoneBelowPerHour);
        Assert.True(ImpactRules.MediumBelowPerDay > ImpactRules.LowBelowPerDay);
    }

    [Fact]
    public void GrowthUnderTheNoneThresholdIsNoneAndAFallIsNone()
    {
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(0));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(ImpactRules.NoneBelowPerHour - 1));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(-5 * ImpactRules.GB));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(long.MinValue));
        Assert.Equal(ImpactLevel.Low, ImpactRules.FromGrowth(ImpactRules.NoneBelowPerHour)); // at the threshold, not below it
    }

    [Fact]
    public void LowEndsWhereADayOfTheRateReachesTheLowLimit()
    {
        long edge = FirstHourlyRateReaching(ImpactRules.LowBelowPerDay);
        Assert.True((edge - 1) * ImpactRules.HoursPerDay < ImpactRules.LowBelowPerDay);
        Assert.Equal(ImpactLevel.Low, ImpactRules.FromGrowth(edge - 1));
        Assert.Equal(ImpactLevel.Medium, ImpactRules.FromGrowth(edge));
    }

    [Fact]
    public void MediumEndsWhereADayOfTheRateReachesTheMediumLimit()
    {
        long edge = FirstHourlyRateReaching(ImpactRules.MediumBelowPerDay);
        Assert.Equal(ImpactLevel.Medium, ImpactRules.FromGrowth(edge - 1));
        Assert.Equal(ImpactLevel.High, ImpactRules.FromGrowth(edge));
        Assert.Equal(ImpactLevel.High, ImpactRules.FromGrowth(ImpactRules.MediumBelowPerDay)); // a day's limit in one hour
    }

    [Fact]
    public void AnEnormousRateIsHighWithoutOverflowing() => Assert.Equal(ImpactLevel.High, ImpactRules.FromGrowth(long.MaxValue));

    private static GrowthWindow Day(TimeSpan measured, GrowthCounts total, IReadOnlyDictionary<PoolTag, GrowthCounts>? tags = null, int clears = 0) => new()
    {
        Span = StatsWindow.Day.Span, NowUtc = T, StartedUtc = T - TimeSpan.FromDays(2), Measured = measured, Total = total, Clears = clears,
        Tags = tags ?? new Dictionary<PoolTag, GrowthCounts>(),
    };

    [Fact]
    public void TheMeasuredLevelIsTheGrowthWithoutTheClearsNeverTheNetChange()
    {
        // The pool grew past a day's Medium limit and PoolWatch's own clears freed nearly all of it: the net change is
        // small (just under the None threshold an hour over the 24 hours), but without the clears it would grow that much
        // a day, which is High.
        long grew = ImpactRules.MediumBelowPerDay + 2 * ImpactRules.GB;
        long net = (ImpactRules.NoneBelowPerHour - 1) * 24;
        var w = Day(TimeSpan.FromHours(24), GrowthCounts.Of(grew, cleared: grew - net), clears: 24);
        Assert.Equal(net, w.Total.Net);
        Assert.Equal(ImpactLevel.High, ImpactRules.FromGrowth(w, w.Total));
        Assert.Equal(ImpactRules.FromGrowth(w.PerHour(w.Total)!.Value), ImpactRules.FromGrowth(w, w.Total)); // the value GrowthText.PerHour shows
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(w.Total.Net / 24)); // what the net change alone would have said
    }

    [Fact]
    public void TheMeasuredLevelIsPerHourMeasuredNotPerHourOfTheWindow()
    {
        // Half the day measured (asleep for the rest): what grew counts over the 12 hours measured.
        long grew = FirstHourlyRateReaching(ImpactRules.LowBelowPerDay) * 12;
        var halfDay = Day(TimeSpan.FromHours(12), GrowthCounts.Of(grew, 0));
        Assert.Equal(ImpactLevel.Medium, ImpactRules.FromGrowth(halfDay, halfDay.Total));
        var wholeDay = Day(TimeSpan.FromHours(24), GrowthCounts.Of(grew, 0));
        var wholeDayLevel = ImpactRules.FromGrowth(wholeDay, wholeDay.Total);
        Assert.Equal(ImpactRules.FromGrowth(grew / 24), wholeDayLevel); // the same growth over twice the hours measured
        Assert.True(wholeDayLevel < ImpactLevel.Medium);
    }

    [Fact]
    public void ThereIsNoMeasuredLevelUntilTheRateIsKnown()
    {
        var early = Day(GrowthWindow.RateAfter - TimeSpan.FromMinutes(1), GrowthCounts.Of(ImpactRules.GB, 0));
        Assert.Null(ImpactRules.FromGrowth(early, early.Total));
        var known = Day(GrowthWindow.RateAfter, GrowthCounts.Of(ImpactRules.GB, 0));
        Assert.NotNull(ImpactRules.FromGrowth(known, known.Total));
        Assert.Null(ImpactRules.FromGrowth(Day(TimeSpan.Zero, default), default)); // nothing measured
    }

    [Fact]
    public void ATagsLevelComesFromItsOwnCounts()
    {
        var reff = PoolTag.Parse("Reff");
        var mmst = PoolTag.Parse("MmSt");
        var tags = new Dictionary<PoolTag, GrowthCounts>
        {
            [reff] = GrowthCounts.Of(ImpactRules.MediumBelowPerDay + ImpactRules.GB, ImpactRules.MediumBelowPerDay),
            [mmst] = GrowthCounts.Of(-ImpactRules.GB, 0),
        };
        long midway = (ImpactRules.LowBelowPerDay + ImpactRules.MediumBelowPerDay) / 2;
        var w = Day(TimeSpan.FromHours(24), GrowthCounts.Of(midway, midway), tags);
        Assert.Equal(ImpactLevel.Medium, ImpactRules.FromGrowth(w, w.Total)); // the pool's own level is a different one
        Assert.Equal(ImpactLevel.High, ImpactRules.FromGrowth(w, w.Tags[reff]));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromGrowth(w, w.Tags[mmst])); // it fell
    }

    [Fact]
    public void ReFSOrADevDriveGivesTheRefsLevel()
    {
        Assert.Equal(ImpactRules.RefsMetadataLevel, ImpactRules.FromFacts.RefsMetadata(refsOrDevDrivePresent: true));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.RefsMetadata(refsOrDevDrivePresent: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void DefenderScanningIsNoneWhenRealTimeProtectionIsOffOrNotKnown(bool? realTimeOn)
    {
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.DefenderScanning(realTimeOn, performanceModeOn: false, [false]));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.DefenderScanning(realTimeOn, performanceModeOn: null, []));
    }

    [Fact]
    public void DefenderScanningWithRealTimeProtectionAndNoDevDriveIsTheNoDevDriveLevel()
    {
        Assert.Equal(ImpactRules.DefenderNoDevDriveLevel, ImpactRules.FromFacts.DefenderScanning(true, performanceModeOn: true, []));
        Assert.Equal(ImpactRules.DefenderNoDevDriveLevel, ImpactRules.FromFacts.DefenderScanning(true, performanceModeOn: null, []));
    }

    [Theory]
    [InlineData(false, true)]   // performance mode off: every Dev Drive is scanned first
    [InlineData(false, null)]
    [InlineData(true, false)]   // performance mode on, but this Dev Drive is not trusted
    [InlineData(null, false)]
    public void ADevDriveWithoutPerformanceModeGivesTheDevDriveLevel(bool? performanceModeOn, bool? trusted)
    {
        Assert.True(ImpactRules.FromFacts.ScannedFirst(performanceModeOn, trusted));
        Assert.Equal(ImpactRules.DefenderDevDriveLevel, ImpactRules.FromFacts.DefenderScanning(true, performanceModeOn, [trusted]));
    }

    [Theory]
    [InlineData(true, true)]    // performance mode on a trusted Dev Drive
    [InlineData(true, null)]    // trust not known: not counted, as the Defender advice reads it
    [InlineData(null, true)]    // performance mode not known
    [InlineData(null, null)]
    public void ADevDriveWithPerformanceModeOrUnknownsIsNone(bool? performanceModeOn, bool? trusted)
    {
        Assert.False(ImpactRules.FromFacts.ScannedFirst(performanceModeOn, trusted));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.DefenderScanning(true, performanceModeOn, [trusted]));
    }

    [Fact]
    public void OneDevDriveScannedFirstAmongSeveralIsEnough() =>
        Assert.Equal(ImpactRules.DefenderDevDriveLevel, ImpactRules.FromFacts.DefenderScanning(true, performanceModeOn: true, [true, false, null]));

    [Fact]
    public void TheCodexShaderCacheFollowsTheDesktopApp()
    {
        Assert.Equal(ImpactRules.CodexShaderCacheLevel, ImpactRules.FromFacts.CodexShaderCache(chatGptDesktopInstalled: true));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.CodexShaderCache(chatGptDesktopInstalled: false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    public void ARunningCatalogueServiceGivesTheServicesLevel(int running) =>
        Assert.Equal(ImpactRules.BackgroundServicesLevel, ImpactRules.FromFacts.BackgroundServices(running));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoRunningCatalogueServiceIsNone(int running) => Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.BackgroundServices(running));

    [Fact]
    public void TheTokenLeakIsOnlyThereWhenTheCheckFoundIt()
    {
        Assert.Equal(ImpactRules.TokenLeakLevel, ImpactRules.FromFacts.TokenLeak(leakFound: true));
        Assert.Equal(ImpactLevel.None, ImpactRules.FromFacts.TokenLeak(leakFound: false)); // unclear or clean: still watching
    }

    [Theory]
    [InlineData(ImpactLevel.High, null, ImpactLevel.High)]     // nothing measured yet: the facts' level
    [InlineData(ImpactLevel.None, null, ImpactLevel.None)]
    [InlineData(ImpactLevel.High, ImpactLevel.Low, ImpactLevel.Low)] // measured lower than the facts suggest
    [InlineData(ImpactLevel.None, ImpactLevel.High, ImpactLevel.High)]
    [InlineData(ImpactLevel.Medium, ImpactLevel.None, ImpactLevel.None)]
    public void TheMeasuredLevelWinsWhenThereIsOne(ImpactLevel facts, ImpactLevel? measured, ImpactLevel shown) =>
        Assert.Equal(shown, ImpactRules.Combine(facts, measured));

    [Fact]
    public void CombineTakesTheMeasuredLevelOfAWindowAsItComes()
    {
        var early = Day(TimeSpan.FromMinutes(10), GrowthCounts.Of(ImpactRules.GB, 0));
        Assert.Equal(ImpactRules.RefsMetadataLevel, ImpactRules.Combine(ImpactRules.FromFacts.RefsMetadata(true), ImpactRules.FromGrowth(early, early.Total)));
        var quiet = Day(TimeSpan.FromHours(24), GrowthCounts.Of(0, 0));
        Assert.Equal(ImpactLevel.None, ImpactRules.Combine(ImpactRules.FromFacts.RefsMetadata(true), ImpactRules.FromGrowth(quiet, quiet.Total)));
    }
}
