using System.Globalization;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Tokens;

/// <summary>One bucket: token-family outstanding allocations at its end, and the process starts during it.</summary>
public sealed record TokenBucket(DateTime EndUtc, IReadOnlyDictionary<PoolTag, long> Outstanding, IReadOnlyDictionary<PoolTag, long> Bytes, int Starts,
    IReadOnlyDictionary<string, int> StartsByOwner);

/// <summary>How one owner's process starts line up with Toke growth.</summary>
public sealed record OwnerTokenStat(string OwnerKey, string Name, int Starts, double Share, double Spearman);

public enum TokenVerdict { NotEnoughData, Flat, TracksStarts, IndependentOfStarts, Unclear }

/// <summary>The current answer to "does every process start leak a token?".</summary>
public sealed record TokenAnalysis(DateTime FromUtc, DateTime ToUtc, int Buckets, long TokeOutstandingDelta, long TokeBytesDelta, int Starts,
    double PerStart, double Spearman, IReadOnlyList<OwnerTokenStat> Owners, IReadOnlyDictionary<string, long> FamilyOutstandingDelta, TokenVerdict Verdict, string Summary);

/// <summary>
/// Tests the "every process start leaks a token" theory separately from the known 25H2 Toke leak: Toke outstanding
/// allocations (allocs minus frees, from SystemPoolTagInformation) per bucket against the number of process starts in
/// the same bucket, overall and per owner (the app, service or Claude Code / AICC session whose processes started them). A normal start allocates a token and frees it at exit, so outstanding
/// allocations that climb in step with starts (and stay) point at the starts; Toke climbing on its own does not.
/// Sampler thread only.
/// </summary>
public sealed class TokenCorrelator
{
    private static readonly PoolTag Toke = PoolTag.Parse("Toke");
    /// <summary>Fewer starts than this in the window cannot carry a "tracks starts" verdict.</summary>
    private const int MinStarts = 20;
    private readonly Settings _settings;
    private readonly ProcessRegistry _registry;
    private readonly List<TokenBucket> _buckets = new();
    private readonly Dictionary<string, int> _byOwner = new();
    private readonly Dictionary<string, string> _ownerNames = new();
    /// <summary>Owner of each parent instance (its chain is walked once, not per start).</summary>
    private readonly Dictionary<ProcessKey, Owner> _ownerOfParent = new();
    private int _starts;
    private DateTime _bucketStart = DateTime.MinValue;
    private DateTime _lastFinding = DateTime.MinValue;
    private TokenVerdict _lastVerdict = TokenVerdict.NotEnoughData;

    public TokenCorrelator(Settings settings, ProcessRegistry registry)
    {
        _settings = settings;
        _registry = registry;
    }

    public TokenAnalysis? Latest { get; private set; }
    public IReadOnlyList<TokenBucket> Buckets => _buckets;

    /// <summary>Outstanding allocations of a tag (paged + non-paged); the 32-bit counters wrap, the difference does not.</summary>
    public static long Outstanding(PoolTagEntry e) => (long)unchecked((int)(e.PagedAllocs - e.PagedFrees)) + unchecked((int)(e.NonPagedAllocs - e.NonPagedFrees));

    /// <summary>Feeds one sample and the starts seen since the previous one. Returns a finding when the verdict is worth reporting.</summary>
    public Finding? Observe(Sample s, IEnumerable<LifecycleEvent> events)
    {
        foreach (var e in events)
        {
            if (e.Kind != LifecycleKind.Start || e.Pid <= 4) continue;
            _starts++;
            var parent = _registry.Get(e.Key)?.ParentKey ?? _registry.GetLive(e.ParentPid)?.Key ?? new ProcessKey(e.ParentPid, 0);
            if (!_ownerOfParent.TryGetValue(parent, out var owner))
            {
                var chain = _registry.Ancestry(parent);
                owner = chain.Count > 0 ? ProcessIdentities.OwnerOf(chain) : new Owner(OwnerKind.Unknown, $"pid:{e.ParentPid}", $"PID {e.ParentPid}");
                if (_ownerOfParent.Count > 2000) _ownerOfParent.Clear();
                _ownerOfParent[parent] = owner;
            }
            _ownerNames[owner.Key] = owner.Name;
            _byOwner[owner.Key] = _byOwner.GetValueOrDefault(owner.Key) + 1;
        }
        if (_bucketStart == DateTime.MinValue) { CloseBucket(s); _bucketStart = s.TimeUtc; return null; } // the baseline point
        if (s.TimeUtc - _bucketStart < TimeSpan.FromSeconds(_settings.TokenBucketSeconds)) return null;
        _bucketStart = s.TimeUtc;
        CloseBucket(s);
        Latest = Analyse();
        return MaybeFinding(Latest);
    }

    private void CloseBucket(Sample s)
    {
        var outstanding = new Dictionary<PoolTag, long>();
        var bytes = new Dictionary<PoolTag, long>();
        foreach (var t in s.Tags)
        {
            if (!BuiltinTags.TokenFamily.Contains(t.Tag)) continue;
            outstanding[t.Tag] = Outstanding(t);
            bytes[t.Tag] = (long)t.TotalUsed;
        }
        _buckets.Add(new TokenBucket(s.TimeUtc, outstanding, bytes, _starts, new Dictionary<string, int>(_byOwner)));
        _starts = 0;
        _byOwner.Clear();
        var cutoff = s.TimeUtc - TimeSpan.FromMinutes(_settings.TokenWindowMinutes);
        int drop = 0;
        while (drop < _buckets.Count - 1 && _buckets[drop].EndUtc < cutoff) drop++;
        if (drop > 0) _buckets.RemoveRange(0, drop);
        // Names only for owners still in the window (a long uptime sees thousands of short-lived parents).
        if (_ownerNames.Count > 500)
        {
            var keep = _buckets.SelectMany(b => b.StartsByOwner.Keys).ToHashSet();
            foreach (var k in _ownerNames.Keys.Where(k => !keep.Contains(k)).ToList()) _ownerNames.Remove(k);
        }
    }

    /// <summary>The analysis over the buckets currently held (the first bucket is the baseline point).</summary>
    public TokenAnalysis Analyse()
    {
        int n = _buckets.Count - 1;
        var from = _buckets.Count > 0 ? _buckets[0].EndUtc : DateTime.MinValue;
        var to = _buckets.Count > 0 ? _buckets[^1].EndUtc : DateTime.MinValue;
        if (n < 1) return new TokenAnalysis(from, to, 0, 0, 0, 0, double.NaN, double.NaN, [], new Dictionary<string, long>(), TokenVerdict.NotEnoughData, "Not enough data yet.");
        var dOut = new double[n];
        var starts = new double[n];
        for (int i = 1; i <= n; i++)
        {
            dOut[i - 1] = _buckets[i].Outstanding.GetValueOrDefault(Toke) - _buckets[i - 1].Outstanding.GetValueOrDefault(Toke);
            starts[i - 1] = _buckets[i].Starts;
        }
        long delta = _buckets[^1].Outstanding.GetValueOrDefault(Toke) - _buckets[0].Outstanding.GetValueOrDefault(Toke);
        long bytesDelta = _buckets[^1].Bytes.GetValueOrDefault(Toke) - _buckets[0].Bytes.GetValueOrDefault(Toke);
        int total = (int)starts.Sum();
        double r = Stats.Spearman(dOut, starts);
        double perStart = total > 0 ? delta / (double)total : double.NaN;

        var ownerTotals = new Dictionary<string, int>();
        for (int i = 1; i <= n; i++)
            foreach (var (k, c) in _buckets[i].StartsByOwner) ownerTotals[k] = ownerTotals.GetValueOrDefault(k) + c;
        var parents = new List<OwnerTokenStat>();
        foreach (var (k, c) in ownerTotals.OrderByDescending(kv => kv.Value).Take(5))
        {
            var series = new double[n];
            for (int i = 1; i <= n; i++) series[i - 1] = _buckets[i].StartsByOwner.GetValueOrDefault(k);
            parents.Add(new OwnerTokenStat(k, _ownerNames.GetValueOrDefault(k, k), c, total > 0 ? c / (double)total : 0, Stats.Spearman(dOut, series)));
        }
        var family = BuiltinTags.TokenFamily.ToDictionary(t => t.ToString(), t => _buckets[^1].Outstanding.GetValueOrDefault(t) - _buckets[0].Outstanding.GetValueOrDefault(t));

        TokenVerdict verdict;
        if (n < _settings.TokenMinBuckets) verdict = TokenVerdict.NotEnoughData;
        else if (delta < _settings.TokenMinOutstandingRise) verdict = TokenVerdict.Flat;
        else if (total >= MinStarts && r >= 0.6 && perStart >= 0.3) verdict = TokenVerdict.TracksStarts;
        else if (double.IsNaN(r) || r < 0.3) verdict = TokenVerdict.IndependentOfStarts;
        else verdict = TokenVerdict.Unclear;

        string span = Fmt.Duration(to - from);
        string top = parents.Count > 0 ? $"; most starts: {parents[0].Name} ({parents[0].Share:P0} of starts, rises and falls with Toke: {Correlation.OwnerRanking.Moves(parents[0].Spearman)})" : "";
        string summary = verdict switch
        {
            TokenVerdict.NotEnoughData => $"Not enough data yet ({n} of {_settings.TokenMinBuckets} buckets).",
            TokenVerdict.Flat => $"Toke outstanding allocations flat ({delta:+#,0;-#,0;0} in {span}, {total:N0} process starts).",
            TokenVerdict.TracksStarts => $"Toke outstanding allocations track process starts: {delta:+#,0} in {span} over {total:N0} starts ({perStart:0.00} per start, r = {Num(r)}){top}.",
            TokenVerdict.IndependentOfStarts => $"Toke outstanding allocations rose {delta:+#,0} in {span} but not with process starts ({total:N0} starts, r = {Num(r)}): not a per-start leak.",
            _ => $"Toke outstanding allocations rose {delta:+#,0} in {span}; the link with {total:N0} process starts is unclear (r = {Num(r)}, {Num(perStart)} per start){top}.",
        };
        return new TokenAnalysis(from, to, n, delta, bytesDelta, total, perStart, r, parents, family, verdict, summary);
    }

    private Finding? MaybeFinding(TokenAnalysis a)
    {
        if (a.Verdict is not (TokenVerdict.TracksStarts or TokenVerdict.IndependentOfStarts)) return null;
        if (a.Verdict == _lastVerdict && a.ToUtc - _lastFinding < TimeSpan.FromHours(1)) return null;
        _lastVerdict = a.Verdict;
        _lastFinding = a.ToUtc;
        var details = a.Owners.Select(p => $"{p.Name}: {p.Starts:N0} process starts ({p.Share:P0}), r = {Num(p.Spearman)}").ToList();
        details.Add("Family outstanding change: " + string.Join(", ", a.FamilyOutstandingDelta.Select(kv => $"{kv.Key} {kv.Value:+#,0;-#,0;0}")));
        return new Finding
        {
            TimeUtc = a.ToUtc, Kind = FindingKind.TokenSpawn, Severity = a.Verdict == TokenVerdict.TracksStarts ? FindingSeverity.Warning : FindingSeverity.Info,
            Title = a.Summary, Details = details, Image = a.Owners.FirstOrDefault()?.Name,
            Data = ToData(a),
        };
    }

    public static Dictionary<string, object?> ToData(TokenAnalysis a) => new()
    {
        ["verdict"] = a.Verdict.ToString(), ["from"] = a.FromUtc, ["to"] = a.ToUtc, ["buckets"] = a.Buckets, ["tokeOutstandingDelta"] = a.TokeOutstandingDelta,
        ["tokeBytesDelta"] = a.TokeBytesDelta, ["starts"] = a.Starts, ["perStart"] = Round(a.PerStart), ["spearman"] = Round(a.Spearman),
        ["family"] = a.FamilyOutstandingDelta.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["owners"] = a.Owners.Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["key"] = p.OwnerKey, ["starts"] = p.Starts, ["share"] = Round(p.Share), ["spearman"] = Round(p.Spearman) }).ToList(),
    };

    private static double? Round(double v) => double.IsNaN(v) ? null : Math.Round(v, 3);
    private static string Num(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.00", CultureInfo.InvariantCulture);
}
