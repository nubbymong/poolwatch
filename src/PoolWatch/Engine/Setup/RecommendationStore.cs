using System.Globalization;
using System.Text.Json;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Engine.Setup;

/// <summary>
/// What the setup guide remembers between starts, in setup.json in the data root: when the guide was completed, the
/// suggestions the owner hid (key and when, for 30 days), and the Simple view's feature cards the owner hid (for good).
/// Thread-safe. The file is read once, as untrusted: a missing, oversize or malformed one counts as empty (the guide opens
/// again), and only well-formed keys and times are kept (of the feature cards, only the keys of cards there are).
/// </summary>
internal sealed class RecommendationStore
{
    public const string FileName = "setup.json";
    private const int MaxBytes = 64 * 1024, MaxKeys = 200, MaxKeyChars = 200;

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly object _lock = new();
    private DateTime? _completedUtc;
    private Dictionary<string, DateTime> _dismissed = new(StringComparer.Ordinal);
    private HashSet<string> _hiddenFeatures = new(StringComparer.Ordinal);

    public RecommendationStore(AppPaths paths, Log log)
    {
        _paths = paths;
        _log = log;
        Load();
    }

    /// <summary>Always in the data root (never under --out): the guide is about this PC, not one headless run.</summary>
    private string StoreFile => _paths.Guard.CheckReal(Path.Combine(_paths.DataRoot, FileName));

    public bool GuideCompleted { get { lock (_lock) return _completedUtc is not null; } }

    public void MarkGuideCompleted(DateTime utc)
    {
        lock (_lock)
        {
            _completedUtc ??= utc;
            Save();
        }
    }

    /// <summary>A copy of the hidden keys and when each was hidden.</summary>
    public IReadOnlyDictionary<string, DateTime> Dismissed { get { lock (_lock) return new Dictionary<string, DateTime>(_dismissed, StringComparer.Ordinal); } }

    /// <summary>Hides a suggestion's key for <see cref="RecommendationRules.DismissFor"/>; hides older than that are forgotten.</summary>
    public void Dismiss(string key, DateTime utc)
    {
        if (key.Length is 0 or > MaxKeyChars) return;
        lock (_lock)
        {
            _dismissed[key] = utc;
            _dismissed = Bounded(_dismissed, utc);
            Save();
        }
    }

    /// <summary>The feature cards hidden (<see cref="DiscoveryCard.Key"/>): hidden for good.</summary>
    public IReadOnlySet<string> HiddenFeatures { get { lock (_lock) return new HashSet<string>(_hiddenFeatures, StringComparer.Ordinal); } }

    /// <summary>Hides a feature card for good (only the key of one of <see cref="Discovery.All"/>).</summary>
    public void HideFeature(string key)
    {
        if (!IsFeatureKey(key)) return;
        lock (_lock)
        {
            if (!_hiddenFeatures.Add(key)) return;
            Save();
        }
    }

    /// <summary>The keys of the feature cards there are: the only keys kept, so the list is never longer than the cards.</summary>
    private static readonly IReadOnlySet<string> FeatureKeys = Discovery.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

    private static bool IsFeatureKey(string? key) => key is not null && FeatureKeys.Contains(key);

    /// <summary>A JSON string's text, or null when it is not valid text (a lone half of a surrogate pair).</summary>
    private static string? TextOf(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Hides still in force, the newest <see cref="MaxKeys"/>. A time after now (the clock was set back, or the file was
    /// edited) counts as now, so that hide runs out 30 days from now, not 30 days after a time still to come.
    /// </summary>
    private static Dictionary<string, DateTime> Bounded(IEnumerable<KeyValuePair<string, DateTime>> keys, DateTime nowUtc) =>
        keys.Select(kv => kv.Value > nowUtc ? KeyValuePair.Create(kv.Key, nowUtc) : kv)
            .Where(kv => kv.Key.Length is > 0 and <= MaxKeyChars && nowUtc - kv.Value < RecommendationRules.DismissFor)
            .OrderByDescending(kv => kv.Value).Take(MaxKeys).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private void Load()
    {
        try
        {
            string file = StoreFile;
            if (!File.Exists(file)) return;
            if (new FileInfo(file).Length > MaxBytes) { _log.Info("setup: setup.json is too large; ignored"); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("guideCompletedUtc", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var completed))
                _completedUtc = completed.ToUniversalTime();
            if (root.TryGetProperty("dismissed", out var d) && d.ValueKind == JsonValueKind.Object)
                _dismissed = Bounded(d.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.TryGetDateTime(out _))
                    .Select(p => KeyValuePair.Create(p.Name, p.Value.GetDateTime().ToUniversalTime())), DateTime.UtcNow);
            if (root.TryGetProperty("hiddenFeatures", out var h) && h.ValueKind == JsonValueKind.Array)
                // Only the cards' own keys, each once: never longer than the cards, whatever the file holds (a key given twice,
                // or text that is not valid, is never what drops a real one).
                _hiddenFeatures = h.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(TextOf).OfType<string>()
                    .Where(IsFeatureKey).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) { _log.Info("setup: setup.json not read: " + ex.Message); }
    }

    /// <summary>Lock held. Written to a temp file and moved over the old one, through the write policy.</summary>
    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                version = 1,
                guideCompletedUtc = _completedUtc?.ToString("o", CultureInfo.InvariantCulture),
                dismissed = _dismissed.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o", CultureInfo.InvariantCulture)),
                hiddenFeatures = _hiddenFeatures.Order(StringComparer.Ordinal).ToList(),
            }, new JsonSerializerOptions { WriteIndented = true });
            string file = StoreFile;
            File.WriteAllText(_paths.Guard.CheckReal(file + ".tmp"), json);
            File.Move(_paths.Guard.CheckReal(file + ".tmp"), _paths.Guard.CheckReal(file), overwrite: true);
        }
        catch (Exception ex) { _log.Info("setup: setup.json not saved: " + ex.Message); }
    }
}
