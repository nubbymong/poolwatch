using System.Globalization;
using System.Text;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Spawn;

/// <summary>One child process start as seen by the spawn tracker.</summary>
public sealed class SpawnRecord
{
    public required ProcessKey Child { get; init; }
    public ProcessKey? Parent { get; init; }
    public int ParentPid { get; init; }
    public required string Image { get; init; }
    public string? CommandLine { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime? ExitUtc { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>For git.exe: the directory it worked in and where that came from ("-C", "cwd", "parent cwd").</summary>
    public string? WorkDir { get; set; }
    public string? WorkDirSource { get; set; }
    public string? GitRoot { get; set; }

    public TimeSpan? Lifetime => ExitUtc is { } e ? e - StartUtc : null;
    public bool ShortLived(TimeSpan limit) => Lifetime is { } l && l <= limit;
}

/// <summary>A parent that launched more short-lived children than the threshold in one window, and what it launched.</summary>
public sealed class SpawnStorm
{
    public const int MaxTrace = 2000, MaxKeys = 500;
    public required ProcessKey ParentKey { get; init; }
    public required int ParentPid { get; init; }
    public required string ParentImage { get; init; }
    public string? ParentCommandLine { get; init; }
    public string ParentChain { get; init; } = "";
    public DateTime OpenedUtc { get; init; }
    public DateTime LastAboveUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public int PeakShortLived { get; set; }
    public int PeakStarts { get; set; }
    public int TotalStarts { get; private set; }
    public Dictionary<string, int> ByImage { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Peak starts per child image within one window.</summary>
    public Dictionary<string, int> PeakByImage { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Patterns { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (GitRootInfo Info, int Count)> GitRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SpawnRecord> Trace { get; } = new();
    public string? ReportDir { get; set; }
    public DateTime LastWriteUtc { get; set; }
    /// <summary>Trace rows already in spawns.csv (the file is rewritten only when this changes).</summary>
    public int TraceWritten { get; set; } = -1;
    /// <summary>The most recent finding about this storm (gets the report folder once it is written).</summary>
    public Finding? LastFinding { get; set; }

    public bool IsOpen => ClosedUtc is null;

    internal void Add(SpawnRecord r, GitRootInfo? root)
    {
        TotalStarts++;
        Bump(ByImage, r.Image);
        Bump(Patterns, CommandLineTokens.Pattern(r.Image, r.CommandLine));
        if (root is not null && (GitRoots.ContainsKey(root.Root) || GitRoots.Count < MaxKeys))
            GitRoots[root.Root] = (root, GitRoots.TryGetValue(root.Root, out var g) ? g.Count + 1 : 1);
        if (Trace.Count < MaxTrace) Trace.Add(r);
    }

    /// <summary>Counts a key; past <see cref="MaxKeys"/> distinct keys new ones go to "(other)" so a storm that never ends stays bounded.</summary>
    private static void Bump(Dictionary<string, int> d, string key)
    {
        if (!d.ContainsKey(key) && d.Count >= MaxKeys) key = "(other)";
        d[key] = d.GetValueOrDefault(key) + 1;
    }
}

/// <summary>
/// Counts process starts per (parent, child image) over a sliding window, from the lifecycle events the collector
/// already produces (no extra tracing). A parent that launches more than <see cref="Settings.SpawnStormThreshold"/>
/// short-lived children within <see cref="Settings.SpawnStormWindowSeconds"/> opens a spawn storm, which stays
/// open until the rate falls below half the threshold for a minute. For git.exe starts the repository root is
/// resolved and flagged when it is a user profile, a OneDrive folder, a volume root or an empty repository.
/// Sampler thread only.
/// </summary>
public sealed class SpawnTracker
{
    private readonly Settings _settings;
    private readonly ProcessRegistry _registry;
    private readonly GitRootResolver _git;
    private readonly GitRootContext _ctx;
    private readonly Func<ProcessKey, string?> _cwdOf;
    private readonly Dictionary<ProcessKey, SpawnRecord> _recent = new();
    private readonly Dictionary<ProcessKey, SpawnStorm> _open = new();
    private readonly Dictionary<string, GitRootInfo> _rootInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _rootReported = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CloseQuiet = TimeSpan.FromSeconds(60);
    private const int MaxRecent = 20_000;

    /// <param name="cwdOf">Working directory of a process instance (PEB read on Windows), or null.</param>
    public SpawnTracker(Settings settings, ProcessRegistry registry, GitRootResolver git, GitRootContext ctx, Func<ProcessKey, string?> cwdOf)
    {
        _settings = settings;
        _registry = registry;
        _git = git;
        _ctx = ctx;
        _cwdOf = cwdOf;
    }

    public IReadOnlyCollection<SpawnStorm> OpenStorms => _open.Values;
    public long StartsSeen { get; private set; }
    public long GitStartsSeen { get; private set; }
    public int RecentCount => _recent.Count;

    private TimeSpan Window => TimeSpan.FromSeconds(_settings.SpawnStormWindowSeconds);
    private TimeSpan ShortLived => TimeSpan.FromSeconds(_settings.ShortLivedSeconds);

    /// <summary>Feeds this tick's lifecycle events; returns findings and the storms whose report should be (re)written.</summary>
    public (List<Finding> findings, List<SpawnStorm> toWrite) Observe(IReadOnlyList<LifecycleEvent> events, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        var toWrite = new List<SpawnStorm>();
        foreach (var e in events)
        {
            if (e.Kind == LifecycleKind.Start && e.Pid > 4) OnStart(e, nowUtc, findings);
            else if (e.Kind == LifecycleKind.Exit && _recent.TryGetValue(e.Key, out var r)) { r.ExitUtc ??= e.TimeUtc; r.ExitCode ??= e.ExitCode; }
        }
        Evaluate(nowUtc, findings, toWrite);
        Prune(nowUtc);
        return (findings, toWrite.Distinct().ToList());
    }

    /// <summary>Starts per (parent, child image) in the current window, busiest first (for the UI).</summary>
    public List<(string Parent, int ParentPid, string Image, int Starts, int ShortLived)> Rates(DateTime nowUtc, int top = 10)
    {
        var from = nowUtc - Window;
        return _recent.Values.Where(r => r.StartUtc >= from)
            .GroupBy(r => (r.Parent, r.ParentPid, r.Image))
            .Select(g => (ParentName(g.Key.Parent, g.Key.ParentPid), g.Key.ParentPid, g.Key.Image, g.Count(), g.Count(r => r.ShortLived(ShortLived))))
            .OrderByDescending(x => x.Item4).Take(top).ToList();
    }

    private string ParentName(ProcessKey? key, int pid) => (key is { } k ? _registry.Get(k)?.Image : null) ?? _registry.GetLive(pid)?.Image ?? "?";

    private void OnStart(LifecycleEvent e, DateTime nowUtc, List<Finding> findings)
    {
        if (_recent.ContainsKey(e.Key)) return;
        StartsSeen++;
        var rec = _registry.Get(e.Key);
        var r = new SpawnRecord
        {
            Child = e.Key, Parent = rec?.ParentKey, ParentPid = e.ParentPid, Image = e.Image, CommandLine = e.CommandLine ?? rec?.CommandLine,
            StartUtc = e.TimeUtc, ExitUtc = rec?.ExitUtc, ExitCode = rec?.ExitCode,
        };
        _recent[e.Key] = r;
        GitRootInfo? info = null;
        if (IsGit(e.Image))
        {
            GitStartsSeen++;
            info = ResolveGit(r, rec, nowUtc);
            if (info is { Suspicious: true } && (!_rootReported.TryGetValue(info.Root, out var last) || nowUtc - last >= TimeSpan.FromHours(1)))
            {
                _rootReported[info.Root] = nowUtc;
                findings.Add(GitRootFinding(info, r, nowUtc));
            }
        }
        if (_open.TryGetValue(ParentKeyOf(r), out var storm)) storm.Add(r, info);
    }

    private static bool IsGit(string image) => image.Equals("git.exe", StringComparison.OrdinalIgnoreCase);

    private GitRootInfo? ResolveGit(SpawnRecord r, ProcessRecord? rec, DateTime nowUtc)
    {
        // -C / --work-tree first, then the child's own cwd (cwd-only PEB read in the ETW start callback, rate-limited, when
        // the process was still there), then the parent's.
        string? cwd = null;
        if (rec is not null) lock (rec.Context) cwd = rec.Context.GetValueOrDefault(Aicc.SessionContext.CwdKey);
        string source = "cwd";
        if (cwd is null && r.Parent is { } pk) { cwd = _cwdOf(pk); source = "parent cwd"; }
        string? dir = GitRootResolver.WorkingDirectory(r.CommandLine, cwd, out bool viaC);
        if (dir is null) return null;
        r.WorkDir = dir;
        r.WorkDirSource = viaC ? "-C" : source;
        if (_git.FindRoot(dir, nowUtc) is not { } root) return null;
        r.GitRoot = root;
        if (!_rootInfo.TryGetValue(root, out var info) || _rootInfo.Count > 1000)
        {
            if (_rootInfo.Count > 1000) _rootInfo.Clear();
            _rootInfo[root] = info = _git.Classify(root, _ctx);
        }
        return info;
    }

    private static ProcessKey ParentKeyOf(SpawnRecord r) => r.Parent ?? new ProcessKey(r.ParentPid, 0);

    private void Evaluate(DateTime nowUtc, List<Finding> findings, List<SpawnStorm> toWrite)
    {
        var from = nowUtc - Window;
        var groups = _recent.Values.Where(r => r.StartUtc >= from).GroupBy(ParentKeyOf).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (pk, list) in groups)
        {
            int shortLived = list.Count(r => r.ShortLived(ShortLived));
            _open.TryGetValue(pk, out var storm);
            bool opened = false;
            if (shortLived > _settings.SpawnStormThreshold && storm is null)
            {
                storm = Open(pk, list, nowUtc);
                _open[pk] = storm;
                opened = true;
            }
            if (storm is null) continue;
            storm.PeakShortLived = Math.Max(storm.PeakShortLived, shortLived);
            storm.PeakStarts = Math.Max(storm.PeakStarts, list.Count);
            foreach (var g in list.GroupBy(r => r.Image, StringComparer.OrdinalIgnoreCase))
                if (storm.PeakByImage.ContainsKey(g.Key) || storm.PeakByImage.Count < SpawnStorm.MaxKeys)
                    storm.PeakByImage[g.Key] = Math.Max(storm.PeakByImage.GetValueOrDefault(g.Key), g.Count());
            if (shortLived >= _settings.SpawnStormThreshold / 2) storm.LastAboveUtc = nowUtc;
            if (opened)
            {
                findings.Add(storm.LastFinding = StormFinding(storm, nowUtc, opened: true));
                toWrite.Add(storm);
            }
            // Disk budget: an open storm's report is refreshed at most every 30 minutes (and at close / exit).
            else if (nowUtc - storm.LastWriteUtc >= TimeSpan.FromMinutes(30)) toWrite.Add(storm);
        }
        foreach (var storm in _open.Values.ToList())
        {
            if (nowUtc - storm.LastAboveUtc < CloseQuiet) continue;
            storm.ClosedUtc = nowUtc;
            _open.Remove(storm.ParentKey);
            findings.Add(storm.LastFinding = StormFinding(storm, nowUtc, opened: false));
            toWrite.Add(storm);
        }
    }

    private SpawnStorm Open(ProcessKey pk, List<SpawnRecord> list, DateTime nowUtc)
    {
        var parent = _registry.Get(pk) ?? _registry.GetLive(pk.Pid);
        var chain = parent is null ? "" : string.Join(" › ", _registry.Ancestry(parent.Key).Select(p => p.DisplayName));
        var storm = new SpawnStorm
        {
            ParentKey = pk, ParentPid = pk.Pid, ParentImage = parent?.DisplayName ?? "?", ParentCommandLine = parent?.CommandLine,
            ParentChain = chain, OpenedUtc = nowUtc, LastAboveUtc = nowUtc,
        };
        foreach (var r in list.OrderBy(r => r.StartUtc))
            storm.Add(r, r.GitRoot is { } g ? _rootInfo.GetValueOrDefault(g) : null);
        return storm;
    }

    private void Prune(DateTime nowUtc)
    {
        var cutoff = nowUtc - Window - ShortLived - TimeSpan.FromSeconds(60);
        if (_recent.Count < 64 && _recent.Values.All(r => r.StartUtc >= cutoff)) return;
        foreach (var k in _recent.Where(kv => kv.Value.StartUtc < cutoff).Select(kv => kv.Key).ToList()) _recent.Remove(k);
        if (_recent.Count > MaxRecent)
            foreach (var k in _recent.Values.OrderBy(r => r.StartUtc).Take(_recent.Count - MaxRecent).Select(r => r.Child).ToList()) _recent.Remove(k);
    }

    // ---- findings and report text ----

    private Finding StormFinding(SpawnStorm s, DateTime nowUtc, bool opened)
    {
        var top = s.ByImage.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value} {kv.Key}");
        string title = opened
            ? $"Spawn storm: {s.ParentImage} (PID {s.ParentPid}) started {s.PeakShortLived} short-lived children in {_settings.SpawnStormWindowSeconds:0} s ({string.Join(", ", top)})"
            : $"Spawn storm ended: {s.ParentImage} (PID {s.ParentPid}) started {s.TotalStarts} processes in {Fmt.Duration((s.ClosedUtc ?? nowUtc) - s.OpenedUtc)} (peak {s.PeakShortLived} short-lived per {_settings.SpawnStormWindowSeconds:0} s)";
        var details = new List<string>();
        foreach (var (pattern, n) in s.Patterns.OrderByDescending(kv => kv.Value).Take(3)) details.Add($"{n}× {pattern}");
        foreach (var (root, (info, n)) in s.GitRoots.OrderByDescending(kv => kv.Value.Count).Take(3))
            details.Add($"git root {root} ({n} starts){(info.Suspicious ? ": " + string.Join(", ", info.Flags) : "")}");
        return new Finding
        {
            TimeUtc = nowUtc, Kind = FindingKind.SpawnStorm, Severity = opened ? FindingSeverity.Alert : FindingSeverity.Info, Title = title,
            Details = details, Pid = s.ParentPid, Image = s.ParentImage, Data = StormData(s),
        };
    }

    public Dictionary<string, object?> StormData(SpawnStorm s) => new()
    {
        ["parent"] = s.ParentImage, ["parentPid"] = s.ParentPid, ["parentCommandLine"] = s.ParentCommandLine, ["parentChain"] = s.ParentChain,
        ["opened"] = s.OpenedUtc, ["closed"] = s.ClosedUtc, ["windowSeconds"] = _settings.SpawnStormWindowSeconds, ["threshold"] = _settings.SpawnStormThreshold,
        ["peakShortLived"] = s.PeakShortLived, ["peakStarts"] = s.PeakStarts, ["totalStarts"] = s.TotalStarts,
        ["byImage"] = s.ByImage.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        ["peakByImage"] = s.PeakByImage.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        ["patterns"] = s.Patterns.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new Dictionary<string, object?> { ["pattern"] = kv.Key, ["count"] = kv.Value }).ToList(),
        ["gitRoots"] = s.GitRoots.OrderByDescending(kv => kv.Value.Count).Select(kv => new Dictionary<string, object?>
        {
            ["root"] = kv.Key, ["count"] = kv.Value.Count, ["flags"] = kv.Value.Info.Flags.ToList(), ["trackedFiles"] = kv.Value.Info.TrackedFiles,
        }).ToList(),
    };

    private static Finding GitRootFinding(GitRootInfo info, SpawnRecord r, DateTime nowUtc) => new()
    {
        TimeUtc = nowUtc, Kind = FindingKind.GitRoot, Severity = FindingSeverity.Warning,
        Title = $"git.exe works in {info.Root}, which is {string.Join(", ", info.Flags)}",
        Details =
        [
            $"Started by PID {r.ParentPid}; working directory {r.WorkDir} (from {r.WorkDirSource})",
            "A repository rooted here makes every status / ls-files scan the whole tree (AppData, OneDrive, caches).",
        ],
        Pid = r.Child.Pid, Image = r.Image,
        Data = new Dictionary<string, object?>
        {
            ["root"] = info.Root, ["flags"] = info.Flags.ToList(), ["trackedFiles"] = info.TrackedFiles, ["workDir"] = r.WorkDir,
            ["workDirSource"] = r.WorkDirSource, ["commandLine"] = r.CommandLine, ["parentPid"] = r.ParentPid,
        },
    };

    /// <summary>report.md body for a storm.</summary>
    public string StormMarkdown(SpawnStorm s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- Parent: **{FindingReport.Md(s.ParentImage)}** (PID {s.ParentPid}){(s.ParentCommandLine is { } cl ? $", `{FindingReport.MdCode(cl)}`" : "")}");
        if (s.ParentChain.Length > 0) sb.AppendLine($"- Parent chain: {FindingReport.Md(s.ParentChain)}");
        sb.AppendLine($"- Open {Fmt.Clock(s.OpenedUtc)} to {(s.ClosedUtc is { } c ? Fmt.Clock(c) : "now (still open)")}; {s.TotalStarts:N0} starts; " +
                      $"peak {s.PeakShortLived} short-lived (exited within {_settings.ShortLivedSeconds:0} s) and {s.PeakStarts} starts per {_settings.SpawnStormWindowSeconds:0} s " +
                      $"(threshold {_settings.SpawnStormThreshold})");
        sb.AppendLine().AppendLine("Every process start creates a token, a process object, a console host and file-system work; a parent that");
        sb.AppendLine("relaunches the same command hundreds of times a minute multiplies all of it. This is a correlation finding, not a proof of a leak.");
        sb.AppendLine().AppendLine("## Children by image").AppendLine().AppendLine("| Image | Starts | Peak per window |").AppendLine("|---|---:|---:|");
        foreach (var (img, n) in s.ByImage.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"| {FindingReport.Md(img)} | {n:N0} | {s.PeakByImage.GetValueOrDefault(img)} |");
        sb.AppendLine().AppendLine("## Top command-line patterns").AppendLine().AppendLine("| Count | Pattern |").AppendLine("|---:|---|");
        foreach (var (p, n) in s.Patterns.OrderByDescending(kv => kv.Value).Take(15))
            sb.AppendLine($"| {n:N0} | `{FindingReport.MdCode(p)}` |");
        if (s.GitRoots.Count > 0)
        {
            sb.AppendLine().AppendLine("## Git repository roots").AppendLine().AppendLine("| Root | git starts | Tracked files | Flags |").AppendLine("|---|---:|---:|---|");
            foreach (var (root, (info, n)) in s.GitRoots.OrderByDescending(kv => kv.Value.Count))
                sb.AppendLine($"| `{FindingReport.MdCode(root)}` | {n:N0} | {info.TrackedFiles?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} | {FindingReport.Md(info.Flags.Count > 0 ? string.Join(", ", info.Flags) : "none")} |");
            if (s.GitRoots.Values.Any(g => g.Info.Suspicious))
                sb.AppendLine().AppendLine("A flagged root (user profile, OneDrive, volume root, empty repository) makes each `git status` / `ls-files` walk far more " +
                                           "than a project. Check for a stray `.git` folder there.");
        }
        sb.AppendLine().AppendLine($"The first {Math.Min(s.Trace.Count, SpawnStorm.MaxTrace):N0} starts are in `spawns.csv`.");
        return sb.ToString();
    }

    /// <summary>spawns.csv: the spawn trace of a storm.</summary>
    public static string TraceCsv(SpawnStorm s)
    {
        var sb = new StringBuilder("time,pid,image,lifetime_ms,exit_code,work_dir,work_dir_source,git_root,command_line\n");
        foreach (var r in s.Trace)
            sb.Append(Util.Csv.Line(r.StartUtc, r.Child.Pid, r.Image,
                r.Lifetime is { } l ? (long)l.TotalMilliseconds : null, r.ExitCode, r.WorkDir, r.WorkDirSource, r.GitRoot,
                r.CommandLine is { Length: > 300 } cl ? cl[..300] + "…" : r.CommandLine)).Append('\n');
        return sb.ToString();
    }
}
