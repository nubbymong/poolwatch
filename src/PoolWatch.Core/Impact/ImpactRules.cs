using PoolWatch.Core.Growth;

namespace PoolWatch.Core.Impact;

/// <summary>
/// The rules that turn what PoolWatch measured, or what its scan of the PC found, into an <see cref="ImpactLevel"/>.
/// Pure: no I/O, no clock, no Windows calls. Every number and every level a rule gives is a named constant in this class,
/// so changing the scale means changing a constant here; no caller or window changes. One unit test pins the agreed
/// values on purpose (ImpactRulesTests.TheScaleIsTheAgreedOne) and changes with them, so a retune shows in review.
/// <para>
/// Units: MB and GB are the units the app shows (<c>Fmt.Bytes</c>, <c>PlainWords.Amount</c>): 1 MB is 2^20 bytes and
/// 1 GB is 2^30 bytes. A day is 24 hours of the hourly rate.
/// </para>
/// </summary>
public static class ImpactRules
{
    /// <summary>One megabyte as the app shows it: 2^20 bytes.</summary>
    public const long MB = 1L << 20;

    /// <summary>One gigabyte as the app shows it: 2^30 bytes.</summary>
    public const long GB = 1L << 30;

    /// <summary>How many hours of the hourly rate make a day.</summary>
    public const int HoursPerDay = 24;

    // ---------------------------------------------------------------- measured growth

    /// <summary>Growth of less than 10 MB an hour (falls included) is None.</summary>
    public const long NoneBelowPerHour = 10 * MB;

    /// <summary>Growth of at least 10 MB an hour but less than 5 GB a day is Low.</summary>
    public const long LowBelowPerDay = 5 * GB;

    /// <summary>Growth of at least 5 GB but less than 10 GB a day is Medium; 10 GB a day or more is High.</summary>
    public const long MediumBelowPerDay = 10 * GB;

    /// <summary>
    /// The level of a growth rate in bytes an hour: None under <see cref="NoneBelowPerHour"/>, then Low, Medium or High
    /// by the rate over a day (<see cref="HoursPerDay"/> times the hourly rate) against <see cref="LowBelowPerDay"/> and
    /// <see cref="MediumBelowPerDay"/>. A fall (a negative rate) is None.
    /// <para>
    /// Callers pass the growth WITHOUT PoolWatch's own cache clears: the value <see cref="GrowthWindow.PerHour"/> returns
    /// for a ledger window, which is the kernel pool's change with PoolWatch's own clears added back (Grew less Fell) per
    /// hour measured. For the whole pool that is <c>window.PerHour(window.Total)</c>, the figure the Simple view's row
    /// shows as "Without the clears it would grow X GB/h" (<see cref="GrowthText.PerHour"/>, over the last 24 hours,
    /// <see cref="StatsWindow.Day"/>); for one tag it is <c>window.PerHour(window.Tags[tag])</c>, the Pool causes "Per
    /// hour" column (<see cref="GrowthText.TagPerHour"/> shows the same value when the tag grew). It is never the net
    /// change, which the clears keep small. <see cref="FromGrowth(GrowthWindow, GrowthCounts)"/> takes the window itself.
    /// </para>
    /// </summary>
    public static ImpactLevel FromGrowth(long bytesPerHour)
    {
        if (bytesPerHour < NoneBelowPerHour) return ImpactLevel.None;
        long perDay = bytesPerHour > long.MaxValue / HoursPerDay ? long.MaxValue : bytesPerHour * HoursPerDay;
        return perDay < LowBelowPerDay ? ImpactLevel.Low : perDay < MediumBelowPerDay ? ImpactLevel.Medium : ImpactLevel.High;
    }

    /// <summary>
    /// The measured level of <paramref name="counts"/> over <paramref name="window"/>: <see cref="FromGrowth(long)"/> of
    /// <see cref="GrowthWindow.PerHour"/>. Pass <c>window.Total</c> for the kernel pool, or a tag's counts from
    /// <c>window.Tags</c>, over the window the figure beside it is shown for. Null until the window has
    /// <see cref="GrowthWindow.RateAfter"/> measured: there is no measured level yet, and <see cref="Combine"/> keeps the
    /// level from the facts.
    /// </summary>
    public static ImpactLevel? FromGrowth(GrowthWindow window, GrowthCounts counts) =>
        window.PerHour(counts) is { } perHour ? FromGrowth(perHour) : null;

    // ---------------------------------------------------------------- the levels the facts give

    /// <summary>ReFS standby-pinned file records, when a ReFS volume or a Dev Drive is there.</summary>
    public const ImpactLevel RefsMetadataLevel = ImpactLevel.High;

    /// <summary>Defender scanning developer files, when real-time protection is on and a Dev Drive lacks performance mode.</summary>
    public const ImpactLevel DefenderDevDriveLevel = ImpactLevel.Medium;

    /// <summary>Defender scanning developer files, when real-time protection is on and there is no Dev Drive.</summary>
    public const ImpactLevel DefenderNoDevDriveLevel = ImpactLevel.Low;

    /// <summary>The Codex GPU shader cache, when the ChatGPT (Codex) desktop app is installed.</summary>
    public const ImpactLevel CodexShaderCacheLevel = ImpactLevel.Low;

    /// <summary>Background services, when one PoolWatch can turn off is running.</summary>
    public const ImpactLevel BackgroundServicesLevel = ImpactLevel.Low;

    /// <summary>The Windows token leak, when PoolWatch's check found it.</summary>
    public const ImpactLevel TokenLeakLevel = ImpactLevel.High;

    /// <summary>
    /// One rule per known cause, from what the scan of the PC found (the first-run "How your PC is impacted" step). Each
    /// takes plain values, and a value that could not be read counts as not found: PoolWatch claims no impact it cannot
    /// see.
    /// </summary>
    public static class FromFacts
    {
        /// <summary>
        /// ReFS keeps a record in the kernel pool for each file in the standby cache, and Windows rarely empties it:
        /// <see cref="RefsMetadataLevel"/> when any fixed volume is ReFS or a Dev Drive (every Dev Drive is ReFS), else None.
        /// </summary>
        public static ImpactLevel RefsMetadata(bool refsOrDevDrivePresent) => refsOrDevDrivePresent ? RefsMetadataLevel : ImpactLevel.None;

        /// <summary>
        /// Microsoft Defender checking developer files before they open: when real-time protection is on,
        /// <see cref="DefenderDevDriveLevel"/> if a Dev Drive is scanned first (<see cref="ScannedFirst"/>),
        /// <see cref="DefenderNoDevDriveLevel"/> if there is no Dev Drive at all; else None (real-time protection off or
        /// not known, or every Dev Drive already has performance mode).
        /// </summary>
        /// <param name="realTimeOn">Defender's real-time protection; null when it could not be read (another antivirus, for example).</param>
        /// <param name="performanceModeOn">Defender's Dev Drive performance mode; null when it could not be read.</param>
        /// <param name="devDrivesTrusted">One value per Dev Drive: whether it is trusted, null when that could not be read. Empty when there is none.</param>
        public static ImpactLevel DefenderScanning(bool? realTimeOn, bool? performanceModeOn, IReadOnlyCollection<bool?> devDrivesTrusted)
        {
            if (realTimeOn != true) return ImpactLevel.None;
            if (devDrivesTrusted.Count == 0) return DefenderNoDevDriveLevel;
            return devDrivesTrusted.Any(trusted => ScannedFirst(performanceModeOn, trusted)) ? DefenderDevDriveLevel : ImpactLevel.None;
        }

        /// <summary>
        /// Whether Defender checks a Dev Drive's files before they open (it has no performance mode): performance mode is
        /// off, or this drive is not trusted (performance mode works only on a trusted Dev Drive). A value that could not
        /// be read counts as neither, as the Defender advice reads them, so the drives the advice names are these.
        /// </summary>
        public static bool ScannedFirst(bool? performanceModeOn, bool? trusted) => performanceModeOn == false || trusted == false;

        /// <summary>The Codex GPU shader cache: <see cref="CodexShaderCacheLevel"/> when the ChatGPT (Codex) desktop app is installed, else None.</summary>
        public static ImpactLevel CodexShaderCache(bool chatGptDesktopInstalled) => chatGptDesktopInstalled ? CodexShaderCacheLevel : ImpactLevel.None;

        /// <summary>
        /// Background services: <see cref="BackgroundServicesLevel"/> when any service on PoolWatch's catalogue (one it can
        /// turn off) is running, else None.
        /// </summary>
        /// <param name="catalogueServicesRunning">How many are running (the scan's count of running optional services).</param>
        public static ImpactLevel BackgroundServices(int catalogueServicesRunning) => catalogueServicesRunning > 0 ? BackgroundServicesLevel : ImpactLevel.None;

        /// <summary>
        /// The Windows token leak: <see cref="TokenLeakLevel"/> when PoolWatch's check found the leak on this Windows build,
        /// else None. None covers a check that was unclear or found nothing as well: the caller says PoolWatch is still
        /// watching for it.
        /// </summary>
        public static ImpactLevel TokenLeak(bool leakFound) => leakFound ? TokenLeakLevel : ImpactLevel.None;
    }

    /// <summary>
    /// The level to show: the measured one when there is one (<paramref name="measured"/> not null), else the one the facts
    /// give. What PoolWatch measured on this PC wins over what its facts suggest, in either direction.
    /// </summary>
    public static ImpactLevel Combine(ImpactLevel facts, ImpactLevel? measured) => measured ?? facts;
}
