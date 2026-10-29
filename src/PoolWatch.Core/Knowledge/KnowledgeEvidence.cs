using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Core.Knowledge;

/// <summary>A running program: its file name, and the name of the Microsoft Store package it comes from (null for any other).</summary>
public readonly record struct RunningProcess(string Image, string? StorePackage);

/// <summary>How much a pool tag grew in each pool over <see cref="KnowledgeEvidence.GrowthOver"/>.</summary>
public readonly record struct TagGrowth(long PagedBytes, long NonPagedBytes);

/// <summary>A measured cache clear: when it ran, and how much of each tag it freed.</summary>
public sealed record ClearResult(DateTime TimeUtc, IReadOnlyDictionary<PoolTag, long> Freed);

/// <summary>
/// What the known-issue rules look at, all of it already held by PoolWatch: the cached facts about the PC, the programs
/// running, how much the packs' pool tags grew lately, what the cache clears of the last day freed, and which built-in
/// actions are already in effect. Plain data, so matching stays pure; tests build it directly.
/// </summary>
public sealed record KnowledgeEvidence
{
    /// <summary>Growth is measured over the last hour (PoolWatch's own cache clears in it measured across, as the detector does).</summary>
    public static readonly TimeSpan GrowthWindow = TimeSpan.FromHours(1);
    /// <summary>A shorter span says nothing about how fast a tag grows (a clear's refill, a burst).</summary>
    public static readonly TimeSpan MinGrowthOver = TimeSpan.FromMinutes(10);
    /// <summary>The cache clears whose results count.</summary>
    public static readonly TimeSpan ClearsWithin = TimeSpan.FromDays(1);

    /// <summary>When the evidence was gathered: a cache clear from an earlier day is named as yesterday's.</summary>
    public required DateTime NowUtc { get; init; }
    /// <summary>CurrentBuild.UBR ("26200.9106"); null before the first scan.</summary>
    public string? WindowsBuild { get; init; }
    /// <summary>The file systems of the PC's fixed drives ("NTFS", "ReFS").</summary>
    public IReadOnlyList<string> FileSystems { get; init; } = [];
    public IReadOnlyList<RunningProcess> Running { get; init; } = [];
    /// <summary>The span <see cref="Growth"/> covers; zero when it is shorter than <see cref="MinGrowthOver"/> (no growth is known then).</summary>
    public TimeSpan GrowthOver { get; init; }
    public IReadOnlyDictionary<PoolTag, TagGrowth> Growth { get; init; } = new Dictionary<PoolTag, TagGrowth>();
    public IReadOnlyList<ClearResult> Clears { get; init; } = [];
    /// <summary>The ids of the built-in actions already in effect (<see cref="KnownActions.InEffect"/>).</summary>
    public IReadOnlyList<string> ActionsOn { get; init; } = [];

    /// <summary>
    /// Gathers the evidence from memory: nothing is scanned or queried for it. Call with the history's lock held
    /// (<see cref="History.History.SyncRoot"/>), as for the detector; it measures only <see cref="KnowledgeBase.GrowthTags"/>.
    /// A cache clear's drop is the clear's doing, not a change of the tag: growth is measured as the detector measures it
    /// across PoolWatch's own clears (from the level each left, <paramref name="clearSettles"/> after it, with the growth
    /// before it kept), so a clear can neither hide growth nor make its refill look like growth. Clears count in
    /// <see cref="Clears"/> only once measured with their per-tag results (a clear read back after a restart keeps only the
    /// tags it freed, up to 12; one from an older file has none).
    /// </summary>
    /// <param name="clears">The cache clears PoolWatch holds, oldest first.</param>
    /// <param name="clearSettles">How long after a clear its result is measured (the cache refills meanwhile).</param>
    /// <param name="inputs">What the recommendation rules look at: the time, the cached facts about the PC (null before the first scan) and the settings.</param>
    /// <param name="liveForegroundLockTimeout">Windows' foreground lock timeout as read now (0 stops the token leak); null when it could not be read.</param>
    /// <param name="running">The live processes.</param>
    public static KnowledgeEvidence Gather(KnowledgeBase knowledge, History.History history, IReadOnlyList<ClearRecord> clears, TimeSpan clearSettles,
        RecommendationInputs inputs, uint? liveForegroundLockTimeout, IEnumerable<ProcessRecord> running)
    {
        DateTime nowUtc = inputs.NowUtc;
        var facts = inputs.Facts;
        var (over, growth) = Measure(history, knowledge.GrowthTags, clears, clearSettles);
        return new KnowledgeEvidence
        {
            NowUtc = nowUtc, WindowsBuild = facts?.Build, FileSystems = facts?.Volumes.Select(v => v.FileSystem).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            Running = running.Select(p => new RunningProcess(p.Image, StorePackageOf(p.ImagePath))).Distinct().ToList(),
            GrowthOver = over, Growth = growth,
            Clears = clears.Where(c => c is { After: not null, Error: null, TagsAfter.Count: > 0 } && c.TimeUtc <= nowUtc && nowUtc - c.TimeUtc <= ClearsWithin)
                .Select(c => new ClearResult(c.TimeUtc, FreedByTag(c))).ToList(),
            ActionsOn = KnownActions.InEffect(inputs, liveForegroundLockTimeout),
        };
    }

    /// <summary>
    /// Each tag's rise in each pool over the raw samples of the last hour, on the detector's baseline for PoolWatch's own
    /// clears (<see cref="ClearSpans"/>: each clear's drop added back, the samples inside its interval skipped, a span that
    /// starts inside one starts at its measurement; none while a clear is being measured): the newest value minus the
    /// lowest in the span (an ongoing climb with small dips still counts whole).
    /// </summary>
    private static (TimeSpan Over, Dictionary<PoolTag, TagGrowth> Growth) Measure(History.History h, IReadOnlySet<PoolTag> tags,
        IReadOnlyList<ClearRecord> clears, TimeSpan clearSettles)
    {
        var growth = new Dictionary<PoolTag, TagGrowth>();
        if (h.Count < 2) return (TimeSpan.Zero, growth);
        var last = h.Times[^1];
        var own = new OwnClears(clears, clearSettles);
        if (own.Measuring(last) is not null) return (TimeSpan.Zero, growth); // nothing can be told across it until it is measured
        var spans = own.SpansIn(h);
        int from = spans.Start(h.IndexAtOrAfter(last - GrowthWindow));
        if (from >= h.Count - 1 || last - h.Times[from] < MinGrowthOver) return (TimeSpan.Zero, growth);
        foreach (var tag in tags)
            growth[tag] = new TagGrowth(Rise(h.TagSeries(tag, from, nonPagedOnly: false)), Rise(h.TagSeries(tag, from, nonPagedOnly: true)));
        return (last - h.Times[from], growth);

        long Rise(long[] series)
        {
            spans.Stitch(series, from);
            long min = long.MaxValue;
            for (int i = 0; i < series.Length; i++)
                if (!spans.Inside(from + i)) min = Math.Min(min, series[i]);
            return series[^1] - min;
        }
    }

    /// <summary>What a measured clear freed of each tag it recorded (a tag that grew meanwhile freed nothing).</summary>
    private static Dictionary<PoolTag, long> FreedByTag(ClearRecord c)
    {
        var after = new Dictionary<PoolTag, long>();
        foreach (var (tag, bytes) in c.TagsAfter) after[tag] = bytes;
        var freed = new Dictionary<PoolTag, long>();
        // A tag missing after the clear was no longer listed: it fell to nothing.
        foreach (var (tag, bytes) in c.TagsBefore) freed[tag] = Math.Max(0, bytes - after.GetValueOrDefault(tag));
        return freed;
    }

    /// <summary>"OpenAI.Codex" for ...\WindowsApps\OpenAI.Codex_26.1016.6573.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe; null for any other path.</summary>
    internal static string? StorePackageOf(string? imagePath)
    {
        const string marker = @"\WindowsApps\";
        int at = imagePath?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0) return null;
        string rest = imagePath![(at + marker.Length)..];
        int end = rest.IndexOfAny(['_', '\\']);
        return end > 0 && rest[end] == '_' ? rest[..end] : null;
    }
}
