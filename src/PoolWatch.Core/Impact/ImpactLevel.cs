namespace PoolWatch.Core.Impact;

/// <summary>
/// How much something costs this PC, on one scale: None, Low, Medium, High. A higher level compares greater, so the
/// larger of two levels is the worse one. <see cref="ImpactRules"/> decides the level; the words and colours for each
/// level belong to the window that shows it.
/// </summary>
public enum ImpactLevel
{
    /// <summary>No sign of it on this PC. PoolWatch keeps watching for it.</summary>
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}
