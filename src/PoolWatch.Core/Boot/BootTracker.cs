using System.Globalization;
using System.Text;
using System.Text.Json;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Boot;

/// <summary>A tag's size in a snapshot (bytes).</summary>
public sealed record TagSize(string Tag, long Paged, long NonPaged)
{
    public long Total => Paged + NonPaged;
}

/// <summary>The first full tag snapshot PoolWatch takes after a boot (at logon when it starts with the logon task).</summary>
public sealed record BootBaseline(string BootId, DateTime BootUtc, DateTime TimeUtc, double UptimeMinutes, long Paged, long NonPaged,
    ulong CommitBytes, uint Handles, uint Processes, List<TagSize> Tags)
{
    /// <summary>Taken within 30 minutes of boot: a clean-boot baseline (later is still the best this boot has).</summary>
    public bool NearBoot => UptimeMinutes <= 30;
}

/// <summary>One boot, summarised: peaks, the biggest tags at the non-paged peak, how long it ran (uptime at the last sample).</summary>
public sealed record BootSummary
{
    public required string BootId { get; init; }
    public DateTime BootUtc { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; set; }
    public double UptimeHoursAtLastSeen { get; set; }
    public long PeakNonPaged { get; set; }
    public DateTime PeakNonPagedUtc { get; set; }
    public long PeakPaged { get; set; }
    public DateTime PeakPagedUtc { get; set; }
    public long BaselineNonPaged { get; set; }
    public long BaselinePaged { get; set; }
    public ulong PeakCommitBytes { get; set; }
    public uint PeakHandles { get; set; }
    public List<TagSize> TopTagsAtPeak { get; set; } = [];
    public int Incidents { get; set; }
    public bool Ended { get; set; }
}

/// <summary>
/// Boot baseline and per-boot summaries kept across reboots (item 13), under <c>boots\&lt;boot id&gt;\</c>: baseline.json
/// once per boot, summary.json rewritten every <see cref="SaveInterval"/> and at exit. The boot id is the boot time to
/// the minute (from the uptime, stable across PoolWatch restarts). <see cref="Table"/> renders the last boots side by side,
/// so a daily climb shows across a week. At most <see cref="KeepBoots"/> boots are kept. Sampler thread only.
/// C6 (review): <see cref="Observe"/> runs under the history lock and only updates memory; the files (a baseline can be
/// hundreds of KB of JSON) are written by <see cref="Flush"/>, called after the lock is released. A failed baseline write
/// is retried after <see cref="RetryAfterFailure"/>, not rebuilt on every sample.
/// </summary>
public sealed class BootTracker
{
    public const int KeepBoots = 60;
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly Func<string, string> _checkWrite;
    private DateTime _lastSave = DateTime.MinValue;
    private int _incidentsBefore; // incidents of earlier PoolWatch runs in this boot
    private List<BootSummary>? _all;
    private BootBaseline? _baselineToWrite;   // taken, not yet on disk
    private DateTime _baselineRetryUtc = DateTime.MinValue;
    private bool _summaryDue, _pruneDue;

    public BootTracker(string bootsDir, Func<string, string> checkWrite)
    {
        _dir = bootsDir;
        _checkWrite = checkWrite;
    }

    public BootBaseline? Baseline { get; private set; }
    public BootSummary? Current { get; private set; }

    /// <summary>"202610260612": the boot time in UTC to the minute.</summary>
    public static string IdOf(DateTime bootUtc) => bootUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

    public static DateTime BootTimeOf(DateTime nowUtc, TimeSpan uptime)
    {
        var b = nowUtc - uptime;
        return new DateTime(b.Ticks - b.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    /// <param name="uptime">System uptime at the sample.</param>
    public void Observe(Sample s, TimeSpan uptime, int incidents)
    {
        if (s.Tags.Count == 0) return;
        var bootUtc = BootTimeOf(s.TimeUtc, uptime);
        string id = IdOf(bootUtc);
        // Uptime is read separately from the sample time: a minute of jitter must not look like a new boot.
        if (Current is { } c && c.BootId != id && Math.Abs((c.BootUtc - bootUtc).TotalMinutes) <= 2) id = c.BootId;
        // The same after a PoolWatch restart: a boot time next to a minute boundary (or a clock correction) must not
        // become a second boot folder.
        if (Current is null && NearbyBoot(bootUtc) is { } existing) id = existing;
        if (Current is null || Current.BootId != id) Start(s, id, bootUtc, uptime);
        var cur = Current!;
        long np = (long)s.Totals.CommittedNonPaged, pg = (long)s.Totals.CommittedPaged;
        cur.LastSeenUtc = s.TimeUtc;
        cur.UptimeHoursAtLastSeen = Math.Round(uptime.TotalHours, 2);
        cur.Incidents = _incidentsBefore + incidents;
        if (np > cur.PeakNonPaged) { cur.PeakNonPaged = np; cur.PeakNonPagedUtc = s.TimeUtc; cur.TopTagsAtPeak = Top(s, 10); }
        if (pg > cur.PeakPaged) { cur.PeakPaged = pg; cur.PeakPagedUtc = s.TimeUtc; }
        if (s.System.CommitBytes > cur.PeakCommitBytes) cur.PeakCommitBytes = s.System.CommitBytes;
        if (s.System.Handles > cur.PeakHandles) cur.PeakHandles = s.System.Handles;
        if (s.TimeUtc - _lastSave >= SaveInterval) { _lastSave = s.TimeUtc; _summaryDue = true; } // written by Flush
    }

    /// <summary>
    /// Writes what <see cref="Observe"/> left due: the boot's baseline (once; after a failure, again only after
    /// <see cref="RetryAfterFailure"/>), the summary every <see cref="SaveInterval"/>, and the pruning of old boots. Call it
    /// outside the history lock (sampler thread). Never throws: returns the failure, if any, for the log.
    /// </summary>
    public string? Flush(DateTime nowUtc)
    {
        string? error = null;
        if (_baselineToWrite is { } b && nowUtc >= _baselineRetryUtc)
        {
            try { WriteBaseline(b); }
            catch (Exception ex)
            {
                _baselineRetryUtc = nowUtc + RetryAfterFailure;
                error = $"boot baseline not written (retry in {RetryAfterFailure.TotalMinutes:0} min): {ex.Message}";
            }
        }
        if (_summaryDue && Current is not null)
        {
            _summaryDue = false; // a failure is retried at the next interval, not on every sample
            try { WriteSummary(ended: false); }
            catch (Exception ex) { error ??= "boot summary not written: " + ex.Message; }
        }
        if (_pruneDue)
        {
            _pruneDue = false;
            try { Prune(); } catch (Exception ex) { error ??= "old boots not pruned: " + ex.Message; }
        }
        return error;
    }

    /// <summary>A baseline is waiting for its file (taken in this run, its write not yet done or failed).</summary>
    public bool BaselinePending => _baselineToWrite is not null;

    private void WriteBaseline(BootBaseline b)
    {
        string dir = Path.Combine(_dir, b.BootId);
        Directory.CreateDirectory(_checkWrite(dir));
        Atomic(Path.Combine(dir, "baseline.json"), JsonSerializer.Serialize(b, Json));
        if (ReferenceEquals(_baselineToWrite, b)) _baselineToWrite = null;
    }

    private void Start(Sample s, string id, DateTime bootUtc, TimeSpan uptime)
    {
        string dir = Path.Combine(_dir, id);
        string baseFile = Path.Combine(dir, "baseline.json"), sumFile = Path.Combine(dir, "summary.json");
        Baseline = TryRead<BootBaseline>(baseFile);
        _baselineToWrite = null;
        _baselineRetryUtc = DateTime.MinValue;
        if (Baseline is null)
        {
            // Taken now and kept in memory even if its file cannot be written: Start runs once per boot, never per sample.
            Baseline = new BootBaseline(id, bootUtc, s.TimeUtc, Math.Round(uptime.TotalMinutes, 1), (long)s.Totals.CommittedPaged, (long)s.Totals.CommittedNonPaged,
                s.System.CommitBytes, s.System.Handles, s.System.Processes,
                s.Tags.Select(t => new TagSize(t.Tag.ToDisplay(), (long)t.PagedUsed, (long)t.NonPagedUsed)).Where(t => t.Total > 0).OrderByDescending(t => t.Total).ToList());
            _baselineToWrite = Baseline;
        }
        // A PoolWatch restart within the same boot continues that boot's summary.
        Current = TryRead<BootSummary>(sumFile) ?? new BootSummary
        {
            BootId = id, BootUtc = bootUtc, FirstSeenUtc = s.TimeUtc, BaselineNonPaged = Baseline.NonPaged, BaselinePaged = Baseline.Paged,
        };
        Current.Ended = false;
        _incidentsBefore = Current.Incidents;
        _lastSave = DateTime.MinValue;
        _pruneDue = true; // directory work is done by Flush, outside the history lock
        _all = null;
    }

    /// <summary>An existing boot folder within two minutes of <paramref name="bootUtc"/>.</summary>
    private string? NearbyBoot(DateTime bootUtc)
    {
        if (!Directory.Exists(_dir)) return null;
        foreach (var d in Directory.EnumerateDirectories(_dir))
        {
            string name = Path.GetFileName(d);
            if (name.Length == 12 && DateTime.TryParseExact(name, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && Math.Abs((t - bootUtc).TotalMinutes) <= 2)
                return name;
        }
        return null;
    }

    private static List<TagSize> Top(Sample s, int n) =>
        s.Tags.OrderByDescending(t => t.TotalUsed).Take(n).Select(t => new TagSize(t.Tag.ToDisplay(), (long)t.PagedUsed, (long)t.NonPagedUsed)).ToList();

    /// <summary>Writes the summary now (and a baseline still waiting for its file), synchronously; throws on failure.</summary>
    /// <param name="ended">PoolWatch is exiting (logoff / shutdown, or the owner closed it).</param>
    public void Save(DateTime nowUtc, bool ended = false)
    {
        _lastSave = nowUtc;
        _summaryDue = false;
        if (_baselineToWrite is { } b) WriteBaseline(b);
        if (Current is null) return;
        WriteSummary(ended);
    }

    private void WriteSummary(bool ended)
    {
        var cur = Current!;
        cur.Ended = ended;
        string dir = Path.Combine(_dir, cur.BootId);
        Directory.CreateDirectory(_checkWrite(dir));
        Atomic(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(cur, Json));
        _all = null;
    }

    /// <summary>Boot summaries on disk, oldest first (at most <see cref="KeepBoots"/>); cached until the next save. Any thread.</summary>
    public List<BootSummary> All()
    {
        if (Volatile.Read(ref _all) is { } cached) return cached;
        var list = new List<BootSummary>();
        if (Directory.Exists(_dir))
            foreach (var d in Directory.EnumerateDirectories(_dir).Order(StringComparer.Ordinal).TakeLast(KeepBoots))
                if (TryRead<BootSummary>(Path.Combine(d, "summary.json")) is { } b) list.Add(b);
        Volatile.Write(ref _all, list);
        return list;
    }

    public List<BootBaseline> Baselines()
    {
        var list = new List<BootBaseline>();
        if (!Directory.Exists(_dir)) return list;
        foreach (var d in Directory.EnumerateDirectories(_dir).Order(StringComparer.Ordinal).TakeLast(KeepBoots))
            if (TryRead<BootBaseline>(Path.Combine(d, "baseline.json")) is { } b) list.Add(b);
        return list;
    }

    /// <summary>boots.md: one row per boot, so the climb across reboots reads at a glance.</summary>
    public static string Table(IReadOnlyList<BootSummary> boots)
    {
        var sb = new StringBuilder("# Boots\n\n");
        sb.AppendLine("| Boot | Ran (h) | Non-paged at start | Peak non-paged | Peak paged | Peak commit | Top tags at the peak | Incidents |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|");
        foreach (var b in boots.OrderByDescending(b => b.BootUtc))
            sb.AppendLine($"| {Fmt.Stamp(b.BootUtc)}{(b.Ended ? "" : " (running or not closed cleanly)")} | {b.UptimeHoursAtLastSeen:0.#} | {Fmt.Bytes(b.BaselineNonPaged)} | " +
                          $"{Fmt.Bytes(b.PeakNonPaged)} | {Fmt.Bytes(b.PeakPaged)} | {Fmt.Bytes((long)b.PeakCommitBytes)} | " +
                          $"{string.Join(", ", b.TopTagsAtPeak.Take(3).Select(t => $"`{t.Tag}` {Fmt.Bytes(t.Total)}"))} | {b.Incidents} |");
        return sb.ToString();
    }

    /// <summary>"Previous boots: peak non-paged 4.1 GB after 20 h, 3.8 GB after 18 h, ..." for the Overview (empty with no previous boot).</summary>
    public string PreviousLine()
    {
        var prev = All().Where(b => b.BootId != Current?.BootId).OrderByDescending(b => b.BootUtc).Take(5).ToList();
        if (prev.Count == 0) return "";
        return "Previous boots: peak non-paged " + string.Join(", ", prev.Select(b => $"{Fmt.Bytes(b.PeakNonPaged)} after {b.UptimeHoursAtLastSeen:0} h")) + ".";
    }

    private void Prune()
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(_dir).Order(StringComparer.Ordinal).ToList();
            foreach (var d in dirs.Take(Math.Max(0, dirs.Count - KeepBoots)))
                if (Path.GetFileName(d).Length == 12 && Path.GetFileName(d).All(char.IsAsciiDigit)) Directory.Delete(_checkWrite(d), true);
        }
        catch (IOException) { }
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void Atomic(string path, string text)
    {
        string tmp = _checkWrite(path + ".tmp");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, _checkWrite(path), overwrite: true);
        Util.Profiler.Wrote(path, text.Length);
    }
}
