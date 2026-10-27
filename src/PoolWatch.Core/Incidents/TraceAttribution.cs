using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Incidents;

/// <summary>One process instance from the confirmation trace, with who it was and who started it.</summary>
public sealed record TracedProcess(CapturePoolUsage Usage, ProcessIdentity Identity, string? CommandLine, IReadOnlyList<string> Chain,
    DateTime? StartUtc, DateTime? ExitUtc, bool Resolved)
{
    /// <summary>Started and exited within <see cref="EventSummarizer.LongLived"/>: a short command.</summary>
    public bool Short => StartUtc is { } s && ExitUtc is { } e && e - s < EventSummarizer.LongLived;

    /// <summary>Its identity, or "unknown process (started HH:MM, exited HH:MM)" when neither PoolWatch nor the trace named it.</summary>
    public string Name => Resolved || (Usage.Image.Length > 0 && !Usage.Image.StartsWith("PID ", StringComparison.Ordinal)) ? Identity.Display : UnknownName(StartUtc, ExitUtc);

    public static string UnknownName(DateTime? start, DateTime? exit) =>
        $"unknown process (started {(start is { } s ? Fmt.Clock(s) : "?")}{(exit is { } e ? $", exited {Fmt.Clock(e)}" : "")})";
}

/// <summary>The traced processes of one owner (a Claude Code session, an app, a service), added up.</summary>
public sealed class TraceOwner
{
    public required Owner Owner { get; init; }
    public List<TracedProcess> Processes { get; } = new();
    public long Net { get; private set; }
    public int Allocs { get; private set; }
    public int Frees { get; private set; }
    /// <summary>The processes that hold a large part of the owner's bytes (at least 1 MB and a tenth of the owner's net).</summary>
    public List<TracedProcess> Big { get; private set; } = [];
    /// <summary>The short commands that are not <see cref="Big"/>.</summary>
    public List<TracedProcess> ShortOnes { get; private set; } = [];
    /// <summary>Average pool kept per short command (bytes; 0 with fewer than three).</summary>
    public long PerShortCommand { get; private set; }

    /// <summary>Computes the figures once, after <see cref="Processes"/> is filled (they are read on every write and UI refresh).</summary>
    public void Seal()
    {
        Net = Processes.Sum(p => Math.Max(0, p.Usage.NetBytes));
        Allocs = Processes.Sum(p => p.Usage.Allocs);
        Frees = Processes.Sum(p => p.Usage.Frees);
        long bigFloor = Math.Max(1L << 20, Net / 10);
        Big = Processes.Where(p => p.Usage.NetBytes >= bigFloor).OrderByDescending(p => p.Usage.NetBytes).ToList();
        var big = Big.ToHashSet(ReferenceEqualityComparer.Instance);
        ShortOnes = Processes.Where(p => p.Short && !big.Contains(p) && p.Usage.NetBytes > 0).ToList();
        PerShortCommand = ShortOnes.Count >= 3 ? (long)ShortOnes.Average(p => p.Usage.NetBytes) : 0;
    }
    /// <summary>Process starts by this owner in the growth window (from the event log; set by the caller).</summary>
    public int StartsInWindow { get; set; }
}

/// <summary>
/// When a confirmation trace exists it outranks correlation (addendum 1, 2a): the answer and the first ranking come from
/// the trace's net bytes per process instance (PID and create time), rolled up by the owner that started each process.
/// </summary>
public static class TraceAttribution
{
    /// <summary>Every traced process instance of <paramref name="tag"/> with its identity (registry record when PoolWatch still has it).</summary>
    public static List<TracedProcess> Resolve(CaptureResult cap, PoolTag tag, ProcessRegistry registry)
    {
        if (!cap.PoolEventsParsed) return [];
        var rows = cap.Pool.Where(u => u.Tag == tag).ToList();
        // One pass over the registry for every PID the trace (and its parents) needs, not one per row.
        var byPid = registry.InstancesOf(rows.Select(u => u.Pid).Concat(rows.Select(u => u.ParentPid)));
        return rows.Select(u => ResolveOne(u, cap, registry, byPid)).ToList();
    }

    /// <summary>The same list with only the rows that were not resolved tried again (records found since).</summary>
    public static List<TracedProcess> Retry(List<TracedProcess> traced, CaptureResult cap, ProcessRegistry registry)
    {
        var missing = traced.Where(t => !t.Resolved && t.Usage.Pid is not (0 or 4)).ToList();
        if (missing.Count == 0) return traced;
        var byPid = registry.InstancesOf(missing.Select(t => t.Usage.Pid).Concat(missing.Select(t => t.Usage.ParentPid)));
        return traced.Select(t => t.Resolved || t.Usage.Pid is 0 or 4 ? t : ResolveOne(t.Usage, cap, registry, byPid)).ToList();
    }

    private static TracedProcess ResolveOne(CapturePoolUsage u, CaptureResult cap, ProcessRegistry registry, IReadOnlyDictionary<int, List<ProcessRecord>> byPid)
    {
        var at = u.FirstAllocUtc == default ? cap.StartUtc : u.FirstAllocUtc;
        var rec = u.Pid is 0 or 4 ? null : ProcessRegistry.Pick(byPid.GetValueOrDefault(u.Pid), u.CreateTime, at);
        List<ProcessRecord> chain;
        if (rec is not null) chain = registry.Ancestry(rec.Key);
        else
        {
            // Not in the registry (the trace saw it, PoolWatch did not): its own record from the trace, under its parent.
            var self = new ProcessRecord { Key = new ProcessKey(u.Pid, u.CreateTime), Image = u.Image, CommandLine = u.CommandLine, ParentPid = u.ParentPid };
            var parent = u.ParentPid > 0 ? ProcessRegistry.Pick(byPid.GetValueOrDefault(u.ParentPid), 0, at) : null;
            chain = parent is not null ? [.. registry.Ancestry(parent.Key), self] : [self];
        }
        if (chain.Count == 0) chain = [rec!];
        // A record kept in compact form (2b), or one whose command line could not be read, takes the trace's own: the
        // identity's role (node <script>, bash -c ...) comes from it. A copy: the registry's record is not changed.
        if (rec is not null && rec.CommandLine is null && u.CommandLine is not null && chain.Count > 0 && chain[^1].Key == rec.Key)
            chain = [.. chain.Take(chain.Count - 1), new ProcessRecord
            {
                Key = rec.Key, ParentPid = rec.ParentPid, ParentKey = rec.ParentKey, Image = rec.Image, ImagePath = rec.ImagePath,
                CommandLine = u.CommandLine, ExitUtc = rec.ExitUtc, Services = rec.Services, Label = rec.Label,
            }];
        var id = ProcessIdentities.Of(chain);
        if (u.Pid is 0 or 4) id = id with { Owner = new Owner(OwnerKind.System, "system", "the kernel (System worker threads)") };
        var start = rec?.StartUtc ?? (u.CreateTime > 0 ? DateTime.FromFileTimeUtc(u.CreateTime) : null);
        return new TracedProcess(u, id, rec?.CommandLine ?? u.CommandLine, chain.Select(r => r.DisplayName).ToList(), start, rec?.ExitUtc ?? u.ExitUtc, rec is not null);
    }

    /// <summary>Traced processes grouped by owner, most net bytes first.</summary>
    public static List<TraceOwner> Owners(IEnumerable<TracedProcess> traced)
    {
        var owners = new Dictionary<string, TraceOwner>();
        foreach (var t in traced)
        {
            if (!owners.TryGetValue(t.Identity.Owner.Key, out var o)) owners[t.Identity.Owner.Key] = o = new TraceOwner { Owner = t.Identity.Owner };
            o.Processes.Add(t);
        }
        foreach (var o in owners.Values) o.Seal();
        return owners.Values.Where(o => o.Net > 0).OrderByDescending(o => o.Net).ToList();
    }

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "git", "grep", "cut", "sed", "awk", "head", "tail", "ls", "find", "cat", "wc", "tr", "xargs", "sort", "uniq", "date", "timeout",
        "conhost", "cmd", "rg", "env", "dirname", "basename", "mkdir", "rm", "cp", "mv", "touch", "tee", "diff", "which", "printf", "echo", "test",
    };

    /// <summary>
    /// "Reff (refs.sys FCB_DATA) grew 580 MB in 26 min and is still held. The trace shows shell commands (grep, cut, git,
    /// bash) allocating it: +147 MB from two commands in 1 min, and about 700 KB per short command, none freed. Started by:
    /// Claude Code session sample-app (PID 4412)." Null when the trace holds no net growth of the tag.
    /// </summary>
    public static string? Answer(Incident inc, TagInfo info, IReadOnlyList<TraceOwner> owners, CaptureResult cap)
    {
        long total = owners.Sum(o => o.Net);
        if (total <= 0 || owners.Count == 0) return null;
        string what = info.Description.Length > 0 && info.Description.Length <= 60 ? $"{inc.Tag.ToDisplay()} ({info.Description})" : inc.Tag.ToDisplay();
        string span = Fmt.Duration((inc.EndUtc ?? inc.LastRiseUtc) - inc.OnsetUtc);
        string held = inc.PeakBytes > 0 && inc.CurrentBytes >= inc.PeakBytes * 9 / 10 ? " and is still held"
            : inc.PeakBytes > inc.CurrentBytes ? $"; {Fmt.Bytes(inc.PeakBytes - inc.CurrentBytes)} has been freed since" : "";
        var top = owners[0];
        string trace = Fmt.Duration(cap.EndUtc - cap.StartUtc);
        var images = top.Processes.OrderByDescending(p => p.Usage.NetBytes).Select(p => Path.GetFileNameWithoutExtension(p.Identity.Image)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string who = images.Count > 0 && images.All(i => ShellTools.Contains(i))
            ? $"shell commands ({string.Join(", ", images.Take(4))}{(images.Count > 4 ? ", ..." : "")})"
            : $"{string.Join(", ", images.Take(3))}{(images.Count > 3 ? ", ..." : "")}";
        var parts = new List<string>();
        var big = top.Big;
        if (big.Count > 0) parts.Add($"{Fmt.SignedBytes(big.Sum(p => p.Usage.NetBytes))} from {Count(big.Count, "command")} in {trace}");
        if (top.PerShortCommand > 0) parts.Add($"about {Fmt.Bytes(top.PerShortCommand)} per short command");
        if (parts.Count == 0) parts.Add($"{Fmt.SignedBytes(top.Net)} in {trace}");
        string freed = top.Frees == 0 ? ", none freed" : top.Allocs > 0 ? $", {100.0 * top.Frees / top.Allocs:0}% of allocations freed" : "";
        string line = $"{what} grew {Fmt.Bytes(Math.Max(0, inc.Growth))} in {span}{held}. The trace shows {who} allocating it: {string.Join(", and ", parts)}{freed}. " +
                      $"Started by: {top.Owner.Name}" + (owners.Count > 1 && owners[1].Net >= total / 5 ? $" ({Fmt.Bytes(top.Net)}); also {owners[1].Owner.Name} ({Fmt.Bytes(owners[1].Net)})." : ".");
        // 2e: the per-start figure explains the growth when the owner's starts in the window times it is a real part of the rise.
        long perStart = top.PerShortCommand;
        if (perStart > 0 && top.StartsInWindow >= 3 && (long)top.StartsInWindow * perStart >= Math.Max(1L << 20, inc.Growth / 10))
            line += $" {top.StartsInWindow:N0} short commands x {Fmt.Bytes(perStart)} = {Fmt.Bytes((long)top.StartsInWindow * perStart)}.";
        return line;
    }

    private static string Count(int n, string noun) => (n switch { 1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", _ => n.ToString("N0") }) + " " + noun + (n == 1 ? "" : "s");

    /// <summary>
    /// 2a: when a confirmation trace exists the ranking follows it, not correlation. The trace's owners come first, by net
    /// bytes (an owner correlation also found keeps its processes and evidence, with the trace's line on top; one only the
    /// trace saw is added), then every other owner below, marked <see cref="OwnerSuspect.CorrelationOnly"/>. The given
    /// correlation owners are updated in place (the caller built them for this view). Ranks are renumbered.
    /// </summary>
    public static List<OwnerSuspect> Rank(IReadOnlyList<TraceOwner> trace, IReadOnlyList<OwnerSuspect> correlation, PoolTag tag)
    {
        var byKey = new Dictionary<string, OwnerSuspect>(StringComparer.Ordinal);
        foreach (var c in correlation) byKey.TryAdd(c.Owner.Key, c);
        var ranked = new List<OwnerSuspect>();
        foreach (var t in trace.Where(t => t.Net > 0).OrderByDescending(t => t.Net))
        {
            var o = byKey.Remove(t.Owner.Key, out var c) ? c : new OwnerSuspect { Owner = t.Owner, Members = [], Identities = [] };
            o.Trace = t;
            o.CorrelationOnly = false;
            o.Confidence = Confidence.ConfirmedByTrace;
            o.Meaningful = true;
            o.ExplainedBytes = Math.Max(o.ExplainedBytes, t.Net);
            string freed = t.Frees == 0 ? "none freed" : t.Allocs > 0 ? $"{100.0 * t.Frees / t.Allocs:0}% of allocations freed" : "";
            o.Plain.RemoveAll(p => p.StartsWith("the confirmation trace", StringComparison.Ordinal));
            string processes = Count(t.Processes.Count, "process").Replace("processs", "processes", StringComparison.Ordinal);
            o.Plain.Insert(0, $"the confirmation trace saw {processes} of it allocate {Fmt.Bytes(t.Net)} of {tag.ToDisplay()}{(freed.Length > 0 ? $", {freed}" : "")}");
            if (t.PerShortCommand > 0) o.Plain.Insert(1, $"about {Fmt.Bytes(t.PerShortCommand)} kept per short command");
            if (o.Plain.Count > 1 && o.Plain[^1] == "no meaningful activity in the window") o.Plain.RemoveAt(o.Plain.Count - 1);
            ranked.Add(o);
        }
        var fromTrace = ranked.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var c in correlation)
            if (!fromTrace.Contains(c))
            {
                c.CorrelationOnly = true;
                ranked.Add(c);
            }
        for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        return ranked;
    }
}
