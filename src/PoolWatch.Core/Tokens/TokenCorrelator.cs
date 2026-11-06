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

/// <summary>
/// How one owner's process starts line up with Toke growth. <paramref name="Kind"/> is the owner's. It is
/// <see cref="OwnerKind.Unknown"/> for a parent whose ancestry PoolWatch could not read, named only "PID n", and for one
/// whose ancestry holds no process PoolWatch names an owner by (only shared runtimes, shells and the like:
/// <see cref="ProcessIdentities.OwnerOf"/>), named "image (PID n)".
/// </summary>
public sealed record OwnerTokenStat(string OwnerKey, string Name, int Starts, double Share, double Spearman, OwnerKind Kind = OwnerKind.App);

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
    /// <summary>A correlation this high (with enough starts and tokens per start) says Toke tracks the starts.</summary>
    private const double TracksBar = 0.6;
    /// <summary>A correlation below this shows no link.</summary>
    private const double NoLinkBar = 0.3;
    private readonly Settings _settings;
    private readonly ProcessRegistry _registry;
    private readonly List<TokenBucket> _buckets = new();
    private readonly Dictionary<string, int> _byOwner = new();
    /// <summary>Each owner counted in the window, by its key: its name and kind.</summary>
    private readonly Dictionary<string, Owner> _owners = new();
    /// <summary>Owner of each parent instance (its chain is walked once, not per start).</summary>
    private readonly Dictionary<ProcessKey, Owner> _ownerOfParent = new();
    private int _starts;
    /// <summary>When each bucket in <see cref="_buckets"/> closed, on the clock that only goes forward (milliseconds).</summary>
    private readonly List<long> _closedMs = new();
    private long? _bucketStartMs;
    private long? _lastFindingMs;
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

    /// <summary>
    /// Feeds one sample and the starts seen since the previous one, taken at <paramref name="nowMs"/> on a clock that only goes
    /// forward (milliseconds, <see cref="Environment.TickCount64"/> in the app). Buckets close, leave the window and repeat a
    /// finding by that clock, and a verdict's summary gives its window's length on it, so a wall clock set back or forward
    /// changes none of them; the sample's own time is only when the analysis says its window began and ended. Returns a
    /// finding when the verdict is worth reporting.
    /// </summary>
    public Finding? Observe(Sample s, IEnumerable<LifecycleEvent> events, long nowMs)
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
            _owners[owner.Key] = owner;
            _byOwner[owner.Key] = _byOwner.GetValueOrDefault(owner.Key) + 1;
        }
        if (_bucketStartMs is not { } started) { CloseBucket(s, nowMs); _bucketStartMs = nowMs; return null; } // the baseline point
        if (nowMs - started < TimeSpan.FromSeconds(_settings.TokenBucketSeconds).TotalMilliseconds) return null;
        _bucketStartMs = nowMs;
        CloseBucket(s, nowMs);
        Latest = Judge();
        return MaybeFinding(Latest, nowMs);
    }

    private void CloseBucket(Sample s, long nowMs)
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
        _closedMs.Add(nowMs);
        _starts = 0;
        _byOwner.Clear();
        double cutoff = nowMs - TimeSpan.FromMinutes(_settings.TokenWindowMinutes).TotalMilliseconds;
        int drop = 0;
        while (drop < _buckets.Count - 1 && _closedMs[drop] < cutoff) drop++;
        if (drop > 0) { _buckets.RemoveRange(0, drop); _closedMs.RemoveRange(0, drop); }
        // Names only for owners still in the window (a long uptime sees thousands of short-lived parents).
        if (_owners.Count > 500)
        {
            var keep = _buckets.SelectMany(b => b.StartsByOwner.Keys).ToHashSet();
            foreach (var k in _owners.Keys.Where(k => !keep.Contains(k)).ToList()) _owners.Remove(k);
        }
    }

    /// <summary>
    /// The verdict after a bucket closes: the whole window's (<see cref="Analyse()"/>), unless a window that begins where a
    /// rise may have begun (<see cref="RiseStarts"/>) shows beyond chance that Toke tracks the starts. A window that begins
    /// at a bucket is what a fresh start of PoolWatch at that bucket would judge, but it is one of up to
    /// <see cref="MaxRiseWindows"/> tried at every bucket close, and each is one more chance for a few buckets of a rise that
    /// ignores the starts to line up with them. So it must hold at least <see cref="MinRiseWindowBuckets"/> buckets and clear
    /// more than the tracking bar: the one-sided 99.9% lower bound of its correlation (<see cref="SpearmanLowerBound"/>) must
    /// reach it. That needs a correlation of 0.9559 over 10 buckets, 0.9292 over 14, 0.8723 over 27.
    /// <para>
    /// The verdict is never later than the whole window alone would give it: the whole window is judged first, at the
    /// tracking bar, at every close. A leak whose own buckets clear the bound as soon as they number and rise enough for a
    /// verdict (each start leaves its token, and nothing else moves Toke in those buckets) is warned of no later than a
    /// fresh start at the bucket before its first rise would warn of it, or than the leak's 10th bucket when that is later
    /// (a fresh start can warn sooner only with <see cref="Settings.TokenMinBuckets"/> under 10): whatever lies before that
    /// bucket in the window (quiet readings, flat, uneven or with dips, or an earlier rise, steeper or not, straight before
    /// it or not), as long as no later reading is below that bucket's (a leak only adds tokens) and it is one of the newest
    /// <see cref="MaxRiseWindows"/> judged then. At the default settings that is the 15th bucket of a leak of about 37
    /// tokens a minute and the 28th of one of about 18; in the tests' rig (ten buckets, a rise of 100), the 10th. With four
    /// buckets per verdict there, a fresh start warns at the 7th; after 10 quiet buckets, or 120, the leak is warned of at
    /// its 10th, and after fewer the whole window can warn of it sooner (after 4, at the 7th; after 9, at the 9th).
    /// </para>
    /// <para>
    /// A noisier leak (other token churn, or readings that wander) is warned of once its own window clears the bound or the
    /// whole window says so at the bar, whichever comes first. The bound is kept at 99.9% so that windows inside the window
    /// add no false warning; the price is that such a leak can be warned of later than a fresh start would, as late as the
    /// whole window alone. Measured at the default settings with the tests' noise model (12 draws, 45 wandering buckets,
    /// then the 37-a-minute leak): readings off by up to 25 tokens either way, the 14th to 38th bucket (the whole window
    /// alone: 23rd to 38th; a fresh start: 14th to 16th); with an earlier rise of 6 x 120 straight before the leak, the 16th
    /// to 52nd (whole window 33rd to 56th, which that rise holds back); with a dip of 100 in the quiet readings that comes
    /// back at the last quiet bucket, the 16th to 44th (whole window 32nd to 44th); off by up to 50, the 33rd to 83rd (whole
    /// window 33rd to 89th, fresh start 14th to 74th), and with that earlier rise, the 45th to 148th; other churn of up to 40
    /// either way, the 13th to 42nd (whole window 27th to 42nd, fresh start 10th to 19th). These hold for those models only;
    /// what holds for any leak is that it is warned of no later than the whole window alone would warn of it.
    /// </para>
    /// Only the warning is taken from these windows: "not a per-start leak" needs the whole window's buckets.
    /// </summary>
    private TokenAnalysis Judge()
    {
        WindowsJudged = 0;
        var whole = Analyse();
        if (whole.Verdict == TokenVerdict.TracksStarts) return whole;
        foreach (int from in RiseStarts())
        {
            WindowsJudged++;
            if (TracksBeyondChance(from) && Analyse(from) is { Verdict: TokenVerdict.TracksStarts } since) return since;
        }
        return whole;
    }

    /// <summary>The most windows that begin inside the window judged at one bucket close (the newest first).</summary>
    internal const int MaxRiseWindows = 32;

    /// <summary>
    /// The fewest buckets a window that begins inside the window is judged on, whatever <see cref="Settings.TokenMinBuckets"/>
    /// says (the Advanced settings allow 4). Over n buckets the starts fall in Toke's exact order by chance once in n! (1 in
    /// 24 over 4, 120 over 5, 720 over 6), and the bound passes that order over any n above 3, so below 7 it does not keep
    /// chance out at 99.9%. Measured: such windows of 4, 5 and 6 buckets raised a false warning in up to 17, 5 and 1 of 40
    /// eight-hour test runs of a kind of rise that ignores the starts, and none from 7 on. Ten is the default, and the count
    /// the bound was measured at.
    /// </summary>
    internal const int MinRiseWindowBuckets = 10;

    /// <summary>The buckets a window that begins inside the window needs: <see cref="Settings.TokenMinBuckets"/>, and at least <see cref="MinRiseWindowBuckets"/>.</summary>
    private int RiseWindowBuckets => Math.Max(_settings.TokenMinBuckets, MinRiseWindowBuckets);

    /// <summary>The windows that begin inside the window judged at the last bucket close (at most <see cref="MaxRiseWindows"/>).</summary>
    internal int WindowsJudged { get; private set; }

    private long TokeAt(int bucket) => _buckets[bucket].Outstanding.GetValueOrDefault(Toke);

    /// <summary>
    /// The buckets a rise in the window is judged from, newest first, at most <see cref="MaxRiseWindows"/>. Walking back from
    /// the newest bucket with the running minimum of the readings after it, each bucket at or below every later reading is
    /// one: every rise is judged against its own lowest reading, so a dip or an earlier rise, which lies before that reading,
    /// cannot move where it is judged from. Of a run of equal readings (a flat quiet spell) only the newest two count, so the
    /// last bucket before a rise and the one before it are both judged from, also when the rise begins with a bucket of no
    /// change. Only a bucket with a verdict's worth after it: <see cref="RiseWindowBuckets"/> buckets
    /// (<see cref="Settings.TokenMinBuckets"/>, and at least <see cref="MinRiseWindowBuckets"/>) and a rise of
    /// <see cref="Settings.TokenMinOutstandingRise"/>. The bucket the window begins at is not one: the whole window is
    /// judged anyway.
    /// </summary>
    internal List<int> RiseStarts()
    {
        var starts = new List<int>();
        int last = _buckets.Count - 1;
        long now = last >= 0 ? TokeAt(last) : 0, low = long.MaxValue;
        int equal = 0;
        int minBuckets = RiseWindowBuckets;
        for (int b = last; b > 0 && starts.Count < MaxRiseWindows; b--)
        {
            long t = TokeAt(b);
            if (t > low) continue; // a later reading is lower: a rise from here is judged from there
            equal = t == low ? equal + 1 : 1;
            low = t;
            if (equal > 2) continue; // the newest two of equal readings are enough
            if (last - b < minBuckets || now - t < _settings.TokenMinOutstandingRise) continue;
            starts.Add(b);
        }
        return starts;
    }

    /// <summary>
    /// True when the buckets from <paramref name="first"/> on, one of <see cref="RiseStarts"/> (so they number
    /// <see cref="RiseWindowBuckets"/> or more and rise enough for a verdict), say Toke tracks the starts, as
    /// <see cref="Analyse(int)"/> would, and the one-sided 99.9% lower bound of their correlation reaches the tracking bar.
    /// Reads only Toke and the start counts: one rank correlation, without the per-owner analysis.
    /// </summary>
    private bool TracksBeyondChance(int first)
    {
        int n = _buckets.Count - 1 - first;
        long delta = TokeAt(_buckets.Count - 1) - TokeAt(first);
        var dOut = new double[n];
        var starts = new double[n];
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            dOut[i] = TokeAt(first + i + 1) - TokeAt(first + i);
            starts[i] = _buckets[first + i + 1].Starts;
            total += _buckets[first + i + 1].Starts;
        }
        double r = Stats.Spearman(dOut, starts);
        return Tracks(total, r, delta) && SpearmanLowerBound(r, n) >= TracksBar;
    }

    /// <summary>The tracking verdict's test, once a window has the buckets and the rise a verdict needs: enough starts, a correlation at the bar, and enough tokens per start.</summary>
    private static bool Tracks(int total, double r, long delta) => total >= MinStarts && r >= TracksBar && delta / (double)total >= 0.3;


    /// <summary>The analysis over the buckets currently held (the first bucket is the baseline point).</summary>
    public TokenAnalysis Analyse() => Analyse(0);

    /// <summary>The analysis over the buckets from <paramref name="first"/> on (that bucket is the baseline point).</summary>
    private TokenAnalysis Analyse(int first)
    {
        var window = _buckets.Skip(first).ToList();
        int n = window.Count - 1;
        var from = window.Count > 0 ? window[0].EndUtc : DateTime.MinValue;
        var to = window.Count > 0 ? window[^1].EndUtc : DateTime.MinValue;
        if (n < 1) return new TokenAnalysis(from, to, 0, 0, 0, 0, double.NaN, double.NaN, [], new Dictionary<string, long>(), TokenVerdict.NotEnoughData, "Not enough data yet.");
        var dOut = new double[n];
        var starts = new double[n];
        for (int i = 1; i <= n; i++)
        {
            dOut[i - 1] = window[i].Outstanding.GetValueOrDefault(Toke) - window[i - 1].Outstanding.GetValueOrDefault(Toke);
            starts[i - 1] = window[i].Starts;
        }
        long delta = window[^1].Outstanding.GetValueOrDefault(Toke) - window[0].Outstanding.GetValueOrDefault(Toke);
        long bytesDelta = window[^1].Bytes.GetValueOrDefault(Toke) - window[0].Bytes.GetValueOrDefault(Toke);
        int total = (int)starts.Sum();
        double r = Stats.Spearman(dOut, starts);
        double perStart = total > 0 ? delta / (double)total : double.NaN;

        var ownerTotals = new Dictionary<string, int>();
        for (int i = 1; i <= n; i++)
            foreach (var (k, c) in window[i].StartsByOwner) ownerTotals[k] = ownerTotals.GetValueOrDefault(k) + c;
        var parents = new List<OwnerTokenStat>();
        foreach (var (k, c) in ownerTotals.OrderByDescending(kv => kv.Value).Take(5))
        {
            var series = new double[n];
            for (int i = 1; i <= n; i++) series[i - 1] = window[i].StartsByOwner.GetValueOrDefault(k);
            var owner = _owners.GetValueOrDefault(k);
            parents.Add(new OwnerTokenStat(k, owner?.Name ?? k, c, total > 0 ? c / (double)total : 0, Stats.Spearman(dOut, series), owner?.Kind ?? OwnerKind.Unknown));
        }
        // Listed first: of the five with the most starts, the owner whose starts rise and fall with Toke the most, at least
        // somewhat, whether PoolWatch could name it or not; never merely the one with the most starts, which can be an app
        // whose starts leave no token while another app's leave one each. The analysis names it only when PoolWatch could.
        if (Tracking(parents) is { } tracking)
        {
            parents.Remove(tracking);
            parents.Insert(0, tracking);
        }
        var family = BuiltinTags.TokenFamily.ToDictionary(t => t.ToString(), t => window[^1].Outstanding.GetValueOrDefault(t) - window[0].Outstanding.GetValueOrDefault(t));

        TokenVerdict verdict;
        if (n < _settings.TokenMinBuckets) verdict = TokenVerdict.NotEnoughData;
        else if (delta < _settings.TokenMinOutstandingRise) verdict = TokenVerdict.Flat;
        else if (Tracks(total, r, delta)) verdict = TokenVerdict.TracksStarts;
        else if (RulesOutPerStart(dOut, starts, r, perStart, total)) verdict = TokenVerdict.IndependentOfStarts;
        else verdict = TokenVerdict.Unclear;

        // How long the window is, on the clock that only goes forward: the samples' wall-clock times show when it began and
        // ended, but a clock set back or forward between them would make this negative or a day too long.
        string span = Fmt.Duration(TimeSpan.FromMilliseconds(_closedMs[^1] - _closedMs[first]));
        string top = Named(parents) is { } p
            ? $"; the starts that rise and fall with Toke most: {p.Name}'s ({p.Share:P0} of starts, {Correlation.OwnerRanking.Moves(p.Spearman)})"
            : Tracking(parents) is { } u
                ? $"; the starts that rise and fall with Toke most are made by a process PoolWatch could not name ({u.Name}, {u.Share:P0} of starts, {Correlation.OwnerRanking.Moves(u.Spearman)})"
                : parents.Count > 0 ? $"; the starts of no single parent rise and fall with Toke (most starts: {parents[0].Name}'s, {parents[0].Share:P0})" : "";
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

    /// <summary>
    /// True when the buckets show that Toke rose apart from the process starts, not merely that they failed to show a link:
    /// "not a per-start leak" is a finding, so a window that cannot tell stays Unclear. Toke rising with no starts at all
    /// rules a per-start leak out. Otherwise three things must hold. The starts varied, and enough that a leak of the
    /// window's tokens per start would have moved Toke with them: if every start left that many tokens, the starts alone
    /// would move Toke from bucket to bucket by x = per start * SD(starts); on top of unrelated churn as large as the SD
    /// Toke's rises actually had, that gives a correlation of x / sqrt(1 + x^2), which must reach the tracking bar (a steady
    /// stream of starts, a poller say, that leaks a token each cannot be told from a steady rise). The correlation is low.
    /// And it is low over enough buckets that a correlation at the tracking bar is outside its one-sided 95% range: near zero,
    /// Spearman over 10 buckets has a standard error of about 0.37, so the first window, which can begin before a leak does or
    /// catch a burst of unrelated token churn (services starting for Windows Update, say), cannot rule a leak out unless
    /// its correlation is close to zero.
    /// </summary>
    private static bool RulesOutPerStart(double[] dOut, double[] starts, double r, double perStart, int total)
    {
        if (total == 0) return true;
        double sdStarts = StdDev(starts), sdOut = StdDev(dOut);
        if (sdStarts == 0) return false; // the same starts in every bucket: nothing to tell a per-start leak by
        if (sdOut > 0)
        {
            double x = perStart * sdStarts / sdOut;
            if (x / Math.Sqrt(1 + x * x) < TracksBar) return false;
        }
        double low = double.IsNaN(r) ? 0 : r; // NaN with varying starts: Toke rose by the same amount in every bucket
        return low < NoLinkBar && SpearmanUpperBound(low, dOut.Length) < TracksBar;
    }

    /// <summary>
    /// One-sided 95% upper bound of a Spearman correlation over n points: Fisher's z with the variance 1.06 / (n - 3) of
    /// Fieller, Hartley and Pearson (1957). It is 0.63 for r = 0.10 over 10 points; from 23 points on it is under 0.6 for
    /// any r under the 0.3 bar.
    /// </summary>
    private static double SpearmanUpperBound(double r, int n) =>
        n <= 3 ? 1 : Math.Tanh(Math.Atanh(Math.Clamp(r, -0.999999, 0.999999)) + 1.645 * Math.Sqrt(1.06 / (n - 3)));

    /// <summary>
    /// One-sided 99.9% lower bound of a Spearman correlation over n points, by the same Fisher's z (z = 3.09). A window that
    /// begins inside the window is one of up to <see cref="MaxRiseWindows"/> tried at every bucket close, so its link with the
    /// starts is believed only when this bound reaches the tracking bar: judged at the bar itself, such windows raised a false
    /// warning in most eight-hour test runs of rises that ignore the starts. The bound reaches 0.6 from r = 0.9559 over 10
    /// points, 0.9292 over 14 and 0.8723 over 27 (pinned); a correlation that cannot be computed (NaN) never does. It is used
    /// only over <see cref="MinRiseWindowBuckets"/> points or more: below 7 it lets a perfect order through, which chance
    /// gives more often than once in a thousand.
    /// </summary>
    internal static double SpearmanLowerBound(double r, int n) =>
        n <= 3 || double.IsNaN(r) ? -1 : Math.Tanh(Math.Atanh(Math.Clamp(r, -0.999999, 0.999999)) - 3.09 * Math.Sqrt(1.06 / (n - 3)));

    private static double StdDev(double[] v)
    {
        double mean = v.Average(), sum = 0;
        foreach (double x in v) sum += (x - mean) * (x - mean);
        return Math.Sqrt(sum / v.Length);
    }

    /// <summary>A correlation at which an owner's starts rise and fall with Toke at least "somewhat" (<see cref="Correlation.OwnerRanking.Moves"/>).</summary>
    public const double NamesOwnerFrom = 0.3;

    /// <summary>
    /// The owner an analysis names (and its finding gives as the program behind it): of all <paramref name="owners"/>, the
    /// one whose starts rise and fall with Toke the most, at <see cref="NamesOwnerFrom"/> or more, the more starts first
    /// among equals, when PoolWatch could name it. Null when no owner's starts do, and when that owner is
    /// <see cref="OwnerKind.Unknown"/> (its name is only a process ID, or an image and a process ID: no owner PoolWatch
    /// could tell): an owner whose starts track Toke less is not given in its place.
    /// </summary>
    public static OwnerTokenStat? Named(IReadOnlyList<OwnerTokenStat> owners) => Tracking(owners) is { Kind: not OwnerKind.Unknown } top ? top : null;

    /// <summary>Of <paramref name="owners"/>, named or not, the one whose starts rise and fall with Toke the most, at <see cref="NamesOwnerFrom"/> or more.</summary>
    private static OwnerTokenStat? Tracking(IEnumerable<OwnerTokenStat> owners) =>
        owners.Where(o => o.Spearman >= NamesOwnerFrom).OrderByDescending(o => o.Spearman).ThenByDescending(o => o.Starts).FirstOrDefault();

    private Finding? MaybeFinding(TokenAnalysis a, long nowMs)
    {
        if (a.Verdict is not (TokenVerdict.TracksStarts or TokenVerdict.IndependentOfStarts)) return null;
        if (a.Verdict == _lastVerdict && nowMs - _lastFindingMs < TimeSpan.FromHours(1).TotalMilliseconds) return null;
        _lastVerdict = a.Verdict;
        _lastFindingMs = nowMs;
        var details = a.Owners.Select(p => $"{p.Name}: {p.Starts:N0} process starts ({p.Share:P0}), r = {Num(p.Spearman)}").ToList();
        details.Add("Family outstanding change: " + string.Join(", ", a.FamilyOutstandingDelta.Select(kv => $"{kv.Key} {kv.Value:+#,0;-#,0;0}")));
        return new Finding
        {
            TimeUtc = a.ToUtc, Kind = FindingKind.TokenSpawn, Severity = a.Verdict == TokenVerdict.TracksStarts ? FindingSeverity.Warning : FindingSeverity.Info,
            Title = a.Summary, Details = details, Image = Named(a.Owners)?.Name,
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
