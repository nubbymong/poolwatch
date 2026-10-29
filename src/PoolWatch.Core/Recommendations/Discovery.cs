namespace PoolWatch.Core.Recommendations;

/// <summary>What a feature card's link opens.</summary>
public enum DiscoveryId { PoolCauses, Services, Mark }

/// <summary>
/// A card under "Get more from PoolWatch" in the Simple view: something in the app the user may not have found, in one
/// sentence, with a link to it and Hide. It changes nothing on the PC. <see cref="Key"/> is what Hide remembers.
/// </summary>
public sealed record DiscoveryCard(DiscoveryId Id, string Title, string Text, string Link)
{
    public string Key => KeyPrefix + Id;

    /// <summary>A hidden card's key starts with this, so it never mixes with an offer's.</summary>
    public const string KeyPrefix = "feature:";
}

/// <summary>The feature cards, in the agreed words, and which still show. Pure.</summary>
public static class Discovery
{
    public static IReadOnlyList<DiscoveryCard> All { get; } =
    [
        new(DiscoveryId.PoolCauses, "See what grows the pool", "Pool causes names the driver or app behind each rise.", "Open Pool causes"),
        new(DiscoveryId.Services, "Trim background services", "Decide with your AI agent which ones this PC needs.", "Open Services"),
        new(DiscoveryId.Mark, "Mark a slow moment", "When the PC feels slow, Mark in the tray menu saves what was happening.", "Show me"),
    ];

    /// <summary>The cards not hidden, in order.</summary>
    public static IReadOnlyList<DiscoveryCard> Shown(IReadOnlySet<string> hidden) => All.Where(c => !hidden.Contains(c.Key)).ToList();

    /// <summary>
    /// What "Show me" on the Mark card says: where Mark is, the shortcut when PoolWatch holds one, and what it keeps (a
    /// note on the timeline at that moment, beside what PoolWatch measured then).
    /// </summary>
    /// <param name="hotkey">The shortcut as the tray menu shows it ("Ctrl+Alt+M"), or null when PoolWatch could not take one.</param>
    public static string MarkHowTo(string? hotkey) =>
        "When the PC feels slow, right-click PoolWatch's icon next to the clock and choose Mark" +
        (hotkey is { Length: > 0 } keys ? $", or press {keys} anywhere" : "") +
        ". Type a few words about what you noticed (\"typing lags\", \"build stalled\"). PoolWatch puts the note on its " +
        "timeline at that moment, beside what it measured then, so the moment is easy to find in an export or a report.";
}
