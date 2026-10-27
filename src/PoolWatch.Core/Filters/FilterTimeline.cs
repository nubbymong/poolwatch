using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Filters;

/// <summary>An event-log record, reduced to what the timeline needs (read with the native EvtQuery / EvtSubscribe API).</summary>
public sealed record LogEvent(DateTime TimeUtc, string Channel, string Provider, int Id, string? Message, IReadOnlyList<string> Properties);

/// <summary>A minifilter instance (fltlib FilterInstanceFindFirst): which filter is attached to which volume.</summary>
public sealed record FilterInstance(string Filter, string Volume, string? Altitude, string? Instance);

/// <summary>
/// Puts file-system filter and container activity on the timeline: FilterManager load / unload events (System log),
/// AppModel-Runtime package activations, the start / stop of vmmem, vmmemCmZygote, vmcompute, vmwp and
/// CoworkVMService (processes and services), and changes in the minifilter instances per volume. A filter that loads
/// within a few seconds of a package activation (bindflt with the Store-packaged app, in one report) gets its own
/// warning. Sampler thread only.
/// </summary>
public sealed partial class FilterTimeline
{
    public const string FilterManagerProvider = "Microsoft-Windows-FilterManager";
    public const string AppModelChannel = "Microsoft-Windows-AppModel-Runtime/Admin";

    /// <summary>Container / VM processes, by image name without .exe (vmmem processes have no extension).</summary>
    public static readonly IReadOnlySet<string> ContainerImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "vmmem", "vmmemCmZygote", "vmcompute", "vmwp", "CoworkVMService" };

    /// <summary>Services (and filter drivers) whose running state is tracked.</summary>
    public static readonly IReadOnlyList<string> WatchedServices = ["vmcompute", "CoworkVMService", "hns", "bindflt", "wcifs"];

    private static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(5);
    private readonly List<(DateTime t, string filter)> _recentLoads = new();
    private readonly List<(DateTime t, string package)> _recentActivations = new();
    private readonly Dictionary<string, DateTime> _activationSeen = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool>? _services;
    private List<FilterInstance>? _instances;
    private bool _initialProcesses;

    public IReadOnlyList<FilterInstance>? Instances => _instances;
    public IReadOnlyDictionary<string, bool>? Services => _services;
    public string? InstancesNote { get; private set; }
    public DateTime? InstancesUtc { get; private set; }
    public int FilterEvents { get; private set; }
    public int PackageEvents { get; private set; }

    public static string Bare(string image) => image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;

    /// <summary>An event-log record: a filter load / unload or a package activation (others are ignored).</summary>
    public List<Finding> OnLogEvent(LogEvent e)
    {
        var findings = new List<Finding>();
        if (e.Provider.Equals(FilterManagerProvider, StringComparison.OrdinalIgnoreCase))
        {
            FilterEvents++;
            string msg = e.Message ?? "";
            string filter = FilterName(e) ?? "?";
            // C6: by event ID, not by the (localised) message text: "unload" was only recognised in English, and event 14
            // ("... successfully loaded filter attach policy ...") read as a filter load.
            bool unload = e.Id == UnloadedEventId;
            bool load = e.Id == LoadedEventId;
            string what = load ? "loaded" : unload ? "unloaded" : $"event {e.Id}";
            findings.Add(new Finding
            {
                TimeUtc = e.TimeUtc, Kind = FindingKind.FilterEvent, Severity = FindingSeverity.Info, Title = $"Filter {filter} {what} (FilterManager event {e.Id})",
                Details = msg.Length > 0 ? [Trim(msg)] : [], Image = filter,
                Data = new Dictionary<string, object?> { ["filter"] = filter, ["eventId"] = e.Id, ["action"] = load ? "load" : unload ? "unload" : "other" },
            });
            if (load)
            {
                _recentLoads.Add((e.TimeUtc, filter));
                foreach (var (t, pkg) in _recentActivations.Where(a => (a.t - e.TimeUtc).Duration() <= PairWindow)) findings.Add(Pair(filter, e.TimeUtc, pkg, t));
            }
        }
        else if (e.Channel.Equals(AppModelChannel, StringComparison.OrdinalIgnoreCase))
        {
            string msg = e.Message ?? "";
            string? package = PackageName(msg) ?? e.Properties.Select(PackageName).FirstOrDefault(p => p is not null);
            if (package is null) return findings;
            // One entry per package per minute: an app launch logs several records.
            if (_activationSeen.TryGetValue(package, out var last) && e.TimeUtc - last < TimeSpan.FromMinutes(1)) return findings;
            _activationSeen[package] = e.TimeUtc;
            PackageEvents++;
            findings.Add(new Finding
            {
                TimeUtc = e.TimeUtc, Kind = FindingKind.PackageActivation, Title = $"Package activity: {package} (AppModel-Runtime event {e.Id})",
                Details = msg.Length > 0 ? [Trim(msg)] : [], Image = package,
                Data = new Dictionary<string, object?> { ["package"] = package, ["eventId"] = e.Id },
            });
            _recentActivations.Add((e.TimeUtc, package));
            foreach (var (t, filter) in _recentLoads.Where(l => (l.t - e.TimeUtc).Duration() <= PairWindow)) findings.Add(Pair(filter, t, package, e.TimeUtc));
        }
        Trim(_recentLoads, e.TimeUtc);
        Trim(_recentActivations, e.TimeUtc);
        return findings;
    }

    private static Finding Pair(string filter, DateTime loaded, string package, DateTime activated) => new()
    {
        TimeUtc = loaded > activated ? loaded : activated, Kind = FindingKind.FilterEvent, Severity = FindingSeverity.Warning,
        Title = $"Filter {filter} loaded {Math.Abs((loaded - activated).TotalSeconds):0} s {(loaded >= activated ? "after" : "before")} package {package} was activated",
        Details = ["An on-demand filter load at app activation means the app's file activity now passes through that filter (bindflt, wcifs: container / package isolation)."],
        Image = filter,
        Data = new Dictionary<string, object?> { ["filter"] = filter, ["package"] = package, ["loaded"] = loaded, ["activated"] = activated },
    };

    private static void Trim<T>(List<(DateTime t, T v)> list, DateTime now) => list.RemoveAll(x => now - x.t > TimeSpan.FromMinutes(2) && x.t < now);

    /// <summary>
    /// Microsoft-Windows-FilterManager event IDs (the provider's manifest, <c>wevtutil gp Microsoft-Windows-FilterManager
    /// /ge /gm</c>): 1 "File System Filter '%5' (Version %2.%3, %6) unloaded successfully", 6 "... '%5' (%2.%3, %6) has
    /// successfully loaded and registered with Filter Manager".
    /// </summary>
    public const int UnloadedEventId = 1, LoadedEventId = 6;

    /// <summary>Which insertion string holds the filter name, by event ID (0-based): %5 for 1 and 4-7, %1 for 11-12; else -1.</summary>
    internal static int FilterNameIndex(int id) => id switch { 1 or 4 or 5 or 6 or 7 => 4, 11 or 12 => 0, _ => -1 };

    /// <summary>The filter name: the event's own insertion string for it (language-independent), else the first
    /// single-quoted word of the message, else the first short property.</summary>
    internal static string? FilterName(LogEvent e)
    {
        int idx = FilterNameIndex(e.Id);
        if (idx >= 0 && idx < e.Properties.Count && e.Properties[idx] is { Length: > 0 and < 64 } named && !named.Contains(' ')) return named;
        if (e.Message is { } m && Quoted().Match(m) is { Success: true } q) return q.Groups[1].Value;
        return e.Properties.FirstOrDefault(p => p.Length is > 0 and < 64 && !p.Contains(' '));
    }

    /// <summary>A package full name (Name_Version_Arch_ResourceId_PublisherId) or family name in a message.</summary>
    internal static string? PackageName(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = PackageFull().Match(text);
        if (m.Success) return m.Groups[1].Value;
        m = PackageFamily().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"'([^'\s]{1,64})'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\b([A-Za-z0-9][A-Za-z0-9.\-]{1,100}_\d+(?:\.\d+){1,3}_(?:x64|x86|arm64|arm|neutral)_[A-Za-z0-9.\-~]*_[a-z0-9]{13})\b")]
    private static partial Regex PackageFull();

    [GeneratedRegex(@"\b([A-Za-z0-9][A-Za-z0-9.\-]{1,100}_[a-z0-9]{13})\b")]
    private static partial Regex PackageFamily();

    private static string Trim(string s) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > 300 ? s[..300] + "…" : s; }

    /// <summary>Starts / exits of container and VM processes (and their state at the first call).</summary>
    /// <param name="live">The live processes, only read at the first call (no per-tick list).</param>
    public List<Finding> OnLifecycle(IReadOnlyList<LifecycleEvent> events, Func<IEnumerable<ProcessRecord>> live, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        if (!_initialProcesses)
        {
            _initialProcesses = true;
            var running = live().Where(r => ContainerImages.Contains(Bare(r.Image))).Select(r => $"{Bare(r.Image)} (PID {r.Pid})").ToList();
            findings.Add(new Finding
            {
                TimeUtc = nowUtc, Kind = FindingKind.Container,
                Title = running.Count > 0 ? "Container / VM processes at start: " + string.Join(", ", running) : "No container / VM processes at start",
                Data = new Dictionary<string, object?> { ["running"] = running, ["initial"] = true },
            });
        }
        foreach (var e in events)
        {
            if (e.Kind is not (LifecycleKind.Start or LifecycleKind.Exit) || !ContainerImages.Contains(Bare(e.Image))) continue;
            bool start = e.Kind == LifecycleKind.Start;
            findings.Add(new Finding
            {
                TimeUtc = e.TimeUtc, Kind = FindingKind.Container, Severity = FindingSeverity.Info, Pid = e.Pid, Image = Bare(e.Image),
                Title = $"{Bare(e.Image)} (PID {e.Pid}) {(start ? "started" : "exited")}{(e.ExitCode is { } c ? $", code {c}" : "")}",
                Data = new Dictionary<string, object?> { ["process"] = Bare(e.Image), ["action"] = start ? "start" : "exit", ["pid"] = e.Pid },
            });
        }
        return findings;
    }

    /// <summary>The running state of the watched services; reports changes (and the state at the first call).</summary>
    public List<Finding> OnServices(IReadOnlyDictionary<string, bool> running, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        if (_services is null)
        {
            findings.Add(new Finding
            {
                TimeUtc = nowUtc, Kind = FindingKind.Container,
                Title = "Watched services at start: " + string.Join(", ", WatchedServices.Select(s => $"{s} {(running.GetValueOrDefault(s) ? "running" : "not running")}")),
                Data = new Dictionary<string, object?> { ["services"] = running.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), ["initial"] = true },
            });
        }
        else
        {
            foreach (var name in WatchedServices)
            {
                bool now = running.GetValueOrDefault(name), was = _services.GetValueOrDefault(name);
                if (now == was) continue;
                findings.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.Container, Image = name, Title = $"Service {name} {(now ? "started" : "stopped")}",
                    Data = new Dictionary<string, object?> { ["service"] = name, ["action"] = now ? "start" : "stop" },
                });
            }
        }
        _services = new Dictionary<string, bool>(running, StringComparer.OrdinalIgnoreCase);
        return findings;
    }

    /// <summary>A new minifilter instance snapshot (null with a note when it could not be taken, e.g. not elevated).</summary>
    private bool _skipReported;

    public List<Finding> OnInstances(IReadOnlyList<FilterInstance>? instances, string? note, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        InstancesNote = note;
        InstancesUtc = nowUtc;
        if (instances is null)
        {
            if (_instances is null && note is not null && !_skipReported)
            {
                _skipReported = true; // once per run, not hourly
                findings.Add(new Finding { TimeUtc = nowUtc, Kind = FindingKind.FilterInstances, Title = "Filter instances per volume: skipped (" + note + ")" });
            }
            return findings;
        }
        static string Key(FilterInstance i) => i.Filter + "@" + i.Volume + "#" + i.Instance;
        if (_instances is null)
        {
            findings.Add(new Finding
            {
                TimeUtc = nowUtc, Kind = FindingKind.FilterInstances,
                Title = $"Filter instances: {instances.Count} on {instances.Select(i => i.Volume).Distinct().Count()} volumes; " + Summary(instances),
                Data = InstanceData(instances),
            });
        }
        else
        {
            var before = _instances.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var after = instances.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = instances.Where(i => !before.Contains(Key(i))).ToList();
            var removed = _instances.Where(i => !after.Contains(Key(i))).ToList();
            if (added.Count + removed.Count > 0)
                findings.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.FilterInstances,
                    Title = "Filter instances changed: " + string.Join("; ", added.Select(i => $"+{i.Filter} on {i.Volume}").Concat(removed.Select(i => $"-{i.Filter} on {i.Volume}")).Take(12)),
                    Data = InstanceData(instances),
                });
        }
        _instances = instances.ToList();
        return findings;
    }

    /// <summary>"wcifs ×3, bindflt ×1, ..." (instances per filter).</summary>
    public static string Summary(IReadOnlyList<FilterInstance> instances) =>
        string.Join(", ", instances.GroupBy(i => i.Filter, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} ×{g.Count()}"));

    public static Dictionary<string, object?> InstanceData(IReadOnlyList<FilterInstance> instances) => new()
    {
        ["instances"] = instances.Select(i => new Dictionary<string, object?> { ["filter"] = i.Filter, ["volume"] = i.Volume, ["altitude"] = i.Altitude, ["instance"] = i.Instance }).ToList(),
        ["perFilter"] = instances.GroupBy(i => i.Filter, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (object?)g.Count()),
    };
}
