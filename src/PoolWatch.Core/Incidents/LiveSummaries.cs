namespace PoolWatch.Core.Incidents;

/// <summary>An open incident's summary from memory (<see cref="LiveSummaries.Get"/>), and when its folder was last written (null: not yet).</summary>
public sealed record LiveSummary(string Json, DateTime? WrittenUtc);

/// <summary>
/// The open incidents' summaries as their report.json would say them now, built from memory
/// (<see cref="IncidentManager.SummaryJson"/>): what the window's details and Copy summary show for an open incident, so
/// they are never behind its folder, which the write budget can hold back. One is built again only when what it says
/// can have changed: the incident was ranked again (every 30 s at most while it changes, which brings its growth, span and
/// suspects up to date), an alert, a re-open, a trace, its end, its suspects' count, or a write of its folder. Its growth
/// alone is not a reason: it moves at nearly every sample, and a build reads the top suspect's Claude Code session, so
/// the summary is at most one ranking behind the growth the list shows, and never behind what its folder says (every
/// write follows a ranking, or changes what is above). The caller holds the history lock.
/// </summary>
public sealed class LiveSummaries
{
    private readonly IncidentManager _incidents;
    private readonly Dictionary<string, (Key Key, LiveSummary Summary)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>At most this many summaries are kept (the window shows one at a time); past it the cache starts again.</summary>
    internal const int MaxKept = 64;

    private readonly record struct Key(long LastRankMs, int Alerts, int Reopened, int TraceOwners, long EndMs, long LastWriteMs, int Suspects);

    public LiveSummaries(IncidentManager incidents) => _incidents = incidents;

    /// <summary>Summaries built so far (tests count them).</summary>
    public int Builds { get; private set; }

    /// <summary>
    /// The summary of the open incident whose folder is <paramref name="dir"/>, the same instance as long as nothing it says
    /// can have changed; null when no open incident has that folder (a closed one, or one read from disk, shows its report.json).
    /// </summary>
    public LiveSummary? Get(History.History h, string dir)
    {
        Incident? inc = null;
        foreach (var i in _incidents.Open)
            if (string.Equals(i.Dir, dir, StringComparison.OrdinalIgnoreCase)) { inc = i; break; }
        if (inc is null)
        {
            _cache.Remove(dir);
            return null;
        }
        var key = new Key(inc.LastRankMs, inc.Alerts.Count, inc.Reopened, inc.TraceOwners?.Count ?? -1, inc.EndMs ?? -1, inc.LastWriteMs, inc.Suspects.Count);
        if (_cache.TryGetValue(dir, out var cached) && cached.Key == key) return cached.Summary;
        var summary = new LiveSummary(_incidents.SummaryJson(h, inc), inc.LastWriteUtc > DateTime.MinValue ? inc.LastWriteUtc : null);
        Builds++;
        if (_cache.Count >= MaxKept) _cache.Clear();
        _cache[dir] = (key, summary);
        return summary;
    }
}
