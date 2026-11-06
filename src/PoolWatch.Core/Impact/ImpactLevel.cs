namespace PoolWatch.Core.Impact;

/// <summary>
/// How much something costs this PC, on one scale: None, Low, Medium, High. A higher level compares greater, so the
/// larger of two levels is the worse one. <see cref="ImpactRules"/> decides the level; the words and colours for each
/// level belong to the window that shows it. Where PoolWatch could not tell (a value it could not read, a check that
/// could not run or gave no clear answer) there is no level: a rule gives null, which setup shows as "Not known". It is
/// not a member here, so it is never compared with, counted as or drawn as one of the levels.
/// </summary>
public enum ImpactLevel
{
    /// <summary>No sign of it on this PC: what decides it was read, or a check ran and found nothing. PoolWatch keeps watching for it.</summary>
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}
