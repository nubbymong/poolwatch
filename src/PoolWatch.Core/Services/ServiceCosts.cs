using System.Globalization;

namespace PoolWatch.Core.Services;

/// <summary>The Cost column of the services page: each running service's process CPU and page faults.</summary>
public static class ServiceCosts
{
    /// <summary>
    /// Fills <see cref="ServiceView.Cores"/>, <see cref="ServiceView.PageFaultsPerSec"/> and <see cref="ServiceView.SharedWith"/>
    /// from the process cost of each running service's PID. Services sharing one host (svchost) all show the host's cost,
    /// and say so: PoolWatch cannot split a shared process between its services.
    /// </summary>
    public static List<ServiceView> Fill(IReadOnlyList<ServiceView> services, Func<int, (double cores, long faultsPerSec)?> costOfPid)
    {
        var perPid = new Dictionary<int, int>();
        foreach (var s in services)
            if (s.Running && s.ProcessId > 0) perPid[s.ProcessId] = perPid.GetValueOrDefault(s.ProcessId) + 1;
        var list = new List<ServiceView>(services.Count);
        foreach (var s in services)
        {
            if (!s.Running || s.ProcessId <= 0) { list.Add(s); continue; }
            var cost = costOfPid(s.ProcessId);
            list.Add(s with
            {
                Cores = cost?.cores ?? 0, PageFaultsPerSec = cost?.faultsPerSec ?? 0, SharedWith = Math.Max(0, perPid.GetValueOrDefault(s.ProcessId) - 1),
            });
        }
        return list;
    }

    /// <summary>A service's share of its host's cost: all of it in its own process, an even part of a shared one.</summary>
    public static (double cores, long faultsPerSec) Share((double cores, long faultsPerSec) host, int servicesInHost) =>
        servicesInHost <= 1 ? host : (host.cores / servicesInHost, host.faultsPerSec / servicesInHost);
}

/// <summary>
/// "Saved about 1.7 cores": after PoolWatch turns a service off, its cost before the change against the
/// average cost of the service's processes over the hour after it (0 while it does not run; it counts again if it
/// restarts itself, as some vendor services do every 30-60 min). Timed in milliseconds on a clock that only goes forward
/// (the caller's: Windows' since it started), so setting the system clock neither stretches nor cuts the hour. In memory:
/// a PoolWatch restart within the hour starts measuring again from the restart. Sampler thread only.
/// </summary>
public sealed class ServiceSavings
{
    public static readonly TimeSpan Span = TimeSpan.FromHours(1);
    /// <summary>A gap longer than this between two observations (sleep, a stalled sampler) is not counted.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(2);
    private static readonly long SpanMs = (long)Span.TotalMilliseconds, MaxGapMs = (long)MaxGap.TotalMilliseconds, DayMs = 24L * 60 * 60 * 1000;

    public sealed class Entry
    {
        public required string Service { get; init; }
        /// <summary>When the change was made, in milliseconds on the caller's clock that only goes forward.</summary>
        public long StartedMs { get; init; }
        public double BeforeCores { get; init; }
        public long BeforeFaultsPerSec { get; init; }
        public double CoreSeconds { get; set; }
        public double Faults { get; set; }
        public double Seconds { get; set; }
        public long LastMs { get; set; }
        public bool Reported { get; set; }
        public double AfterCores => Seconds > 0 ? CoreSeconds / Seconds : 0;
        public long AfterFaultsPerSec => Seconds > 0 ? (long)(Faults / Seconds) : 0;
        public double SavedCores => BeforeCores - AfterCores;
        public bool Complete(long nowMs) => nowMs - StartedMs >= SpanMs;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Entry> Entries => _entries.Values;
    public Entry? Get(string service) => _entries.GetValueOrDefault(service);

    /// <summary>Starts measuring a change made at <paramref name="startedMs"/> (replaces an earlier one of the same service).</summary>
    public void Start(string service, long startedMs, double beforeCores, long beforeFaultsPerSec) =>
        _entries[service] = new Entry { Service = service, StartedMs = startedMs, BeforeCores = Math.Max(0, beforeCores), BeforeFaultsPerSec = Math.Max(0, beforeFaultsPerSec), LastMs = startedMs };

    /// <summary>The change was undone (Restore): nothing more to measure.</summary>
    public void Stop(string service) => _entries.Remove(service);

    /// <summary>
    /// Every sample (<paramref name="nowMs"/>, on the same clock as <see cref="Start"/>): adds the time since the last
    /// observation at the service's cost now (<paramref name="costOf"/>: zero when it is not running). Returns the entries
    /// whose hour just ended (each once), for a finding.
    /// </summary>
    public List<Entry> Observe(long nowMs, Func<string, (double cores, long faultsPerSec)> costOf)
    {
        var done = new List<Entry>();
        foreach (var e in _entries.Values)
        {
            if (e.Reported) continue;
            long end = Math.Min(nowMs, e.StartedMs + SpanMs);
            long gap = end - e.LastMs;
            if (gap > 0 && gap <= MaxGapMs)
            {
                var (cores, faults) = costOf(e.Service);
                double seconds = gap / 1000.0;
                e.CoreSeconds += Math.Max(0, cores) * seconds;
                e.Faults += Math.Max(0, faults) * seconds;
                e.Seconds += seconds;
            }
            if (end > e.LastMs) e.LastMs = end;
            if (e.Complete(nowMs)) { e.Reported = true; done.Add(e); }
        }
        // Entries reported more than a day ago have done their job.
        foreach (var k in _entries.Values.Where(e => e.Reported && nowMs - e.StartedMs > DayMs).Select(e => e.Service).ToList()) _entries.Remove(k);
        return done;
    }

    /// <summary>
    /// "saved about 1.7 cores over the hour after (before 1.7 cores, 887k page faults/s; after idle)", "saved about 1.7
    /// cores so far (35 min measured; ...)", or that it was idle before, or that there is no saving.
    /// </summary>
    public static string Text(Entry e, long nowMs)
    {
        string before = ServicePlan.Cost(e.BeforeCores, e.BeforeFaultsPerSec);
        if (e.Seconds < 60) return $"measuring what it saves (before: {before})";
        string after = ServicePlan.Cost(e.AfterCores, e.AfterFaultsPerSec);
        string span = e.Complete(nowMs) ? "over the hour after" : $"so far ({(int)(e.Seconds / 60)} min measured)";
        if (e.BeforeCores < 0.05 && e.BeforeFaultsPerSec < 1000) return $"no measurable saving: it was idle before the change (after: {after}, {span})";
        double saved = e.SavedCores;
        long savedFaults = e.BeforeFaultsPerSec - e.AfterFaultsPerSec;
        if (saved < 0.05 && savedFaults < 1000) return $"no saving {span}: before {before}, after {after}";
        var parts = new List<string>();
        if (saved >= 0.05) parts.Add($"about {saved.ToString("0.0", CultureInfo.InvariantCulture)} core{(saved < 1.05 ? "" : "s")}");
        if (savedFaults >= 1000) parts.Add($"{ServicePlan.Faults(savedFaults)} page faults/s");
        return $"saved {string.Join(" and ", parts)} {span} (before {before}, after {after})";
    }
}
