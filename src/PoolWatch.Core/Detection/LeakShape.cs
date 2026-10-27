namespace PoolWatch.Core.Detection;

/// <summary>How a tag's allocations and frees move together.</summary>
public enum AllocShape { Idle, LeakShape, GrowingWithChurn, Churn, Shrinking }

/// <summary>Allocation and free rates of a tag over a window, and what their shape says.</summary>
public readonly record struct TagRates(double AllocsPerMin, double FreesPerMin, AllocShape Shape)
{
    public string Text => LeakShapes.Text(Shape);
}

/// <summary>
/// Separates a leak ("allocations rising, frees flat": the tag keeps allocating and almost nothing is freed) from churn
/// (allocations and frees both moving: busy, but returned). Counts come from SystemPoolTagInformation's per-tag
/// alloc / free counters (32-bit, wrap-safe differences).
/// </summary>
public static class LeakShapes
{
    /// <summary>Below this many allocations per minute a tag is idle.</summary>
    public const double MinActivePerMin = 10;

    public static AllocShape Classify(double allocsPerMin, double freesPerMin)
    {
        if (allocsPerMin < MinActivePerMin && freesPerMin < MinActivePerMin) return AllocShape.Idle;
        if (freesPerMin > allocsPerMin * 1.05) return AllocShape.Shrinking;
        if (freesPerMin <= allocsPerMin * 0.2) return AllocShape.LeakShape;
        if (freesPerMin >= allocsPerMin * 0.8) return AllocShape.Churn;
        return AllocShape.GrowingWithChurn;
    }

    /// <summary>
    /// The shape against the tag's own rates just before the growth: a busy tag (File on any PC) has steady background
    /// churn in both counts, which hides a leak in the plain ratio. When the extra allocations are real and the extra frees
    /// stay under a fifth of them, it is the leak shape whatever the background does.
    /// </summary>
    public static AllocShape Classify(double allocsPerMin, double freesPerMin, TagRates? baseline)
    {
        if (baseline is { } b && b.Shape != AllocShape.Idle)
        {
            double extraAllocs = allocsPerMin - b.AllocsPerMin, extraFrees = Math.Max(0, freesPerMin - b.FreesPerMin);
            if (extraAllocs >= MinActivePerMin && extraFrees <= extraAllocs * 0.2) return AllocShape.LeakShape;
        }
        return Classify(allocsPerMin, freesPerMin);
    }

    public static string Text(AllocShape s) => s switch
    {
        AllocShape.LeakShape => "allocations rising, frees flat (leak shape)",
        AllocShape.GrowingWithChurn => "allocating faster than freeing (growing with churn)",
        AllocShape.Churn => "allocations and frees both moving (churn)",
        AllocShape.Shrinking => "freeing more than allocating",
        _ => "idle",
    };

    /// <summary>Rates from two (allocs, frees) counter readings <paramref name="minutes"/> apart; differences taken in 32 bits.</summary>
    public static TagRates Rates((uint allocs, uint frees) from, (uint allocs, uint frees) to, double minutes)
    {
        if (minutes <= 0) return new TagRates(0, 0, AllocShape.Idle);
        double a = unchecked(to.allocs - from.allocs) / minutes, f = unchecked(to.frees - from.frees) / minutes;
        return new TagRates(a, f, Classify(a, f));
    }
}
