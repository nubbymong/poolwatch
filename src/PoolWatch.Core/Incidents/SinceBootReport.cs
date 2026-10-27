using System.Text;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Incidents;

/// <summary>One tag in the since-boot ranking, with its growth attributed hour by hour.</summary>
public sealed record SinceBootTag(PoolTag Tag, string Owner, string Description, long BytesNow, long BytesAtFirstMinute, long GrowthObserved,
    List<SinceBootHour> Hours);

public sealed record SinceBootHour(DateTime HourUtc, long Growth, List<(string Process, int Pid, double Share, long Bytes)> Attribution);

/// <summary>
/// "What has grown since boot, and what drove it, hour by hour" (DESIGN.md section 6). Uses the one-minute history.
/// Pool usage at boot is small next to the daily climb, so a tag's current size is reported as its since-boot
/// growth; the observed growth (first recorded minute to now) is shown beside it. Each hour's growth is attributed
/// across processes by their share of I/O in that hour (activity correlation, not proof).
/// </summary>
public static class SinceBootReport
{
    public static List<SinceBootTag> Build(History.History h, ProcessRegistry registry, TagKnowledge knowledge, int topTags = 15, int perHour = 3)
    {
        var minutes = h.Minutes;
        var result = new List<SinceBootTag>();
        if (minutes.Count == 0) return result;
        var first = minutes[0];
        var last = minutes[^1];
        var nowByTag = new Dictionary<PoolTag, long>();
        for (int i = 0; i < last.Tags.Length; i++) nowByTag[last.Tags[i]] = last.TagBytes[i];
        var firstByTag = new Dictionary<PoolTag, long>();
        for (int i = 0; i < first.Tags.Length; i++) firstByTag[first.Tags[i]] = first.TagBytes[i];

        var hours = minutes.GroupBy(m => new DateTime(m.TimeUtc.Ticks - m.TimeUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc)).OrderBy(g => g.Key).ToList();
        foreach (var (tag, now) in nowByTag.OrderByDescending(kv => kv.Value).Take(topTags))
        {
            var info = knowledge.Lookup(tag);
            var perHourList = new List<SinceBootHour>();
            foreach (var g in hours)
            {
                var ms = g.ToList();
                long start = Bytes(ms[0], tag), end = Bytes(ms[^1], tag);
                long growth = end - start;
                var attribution = new List<(string, int, double, long)>();
                if (growth > 0)
                {
                    var ops = new Dictionary<ProcessKey, long>();
                    foreach (var m in ms)
                        for (int i = 0; i < m.Processes.Length; i++)
                            if (m.Processes[i].Pid != 0) ops[m.Processes[i]] = ops.GetValueOrDefault(m.Processes[i]) + m.ProcessOps[i];
                    long total = ops.Values.Sum();
                    if (total > 0)
                        foreach (var (key, o) in ops.OrderByDescending(kv => kv.Value).Take(perHour))
                        {
                            double share = o / (double)total;
                            attribution.Add((registry.Get(key)?.DisplayName ?? "unknown process", key.Pid, share, (long)(growth * share)));
                        }
                }
                perHourList.Add(new SinceBootHour(g.Key, growth, attribution));
            }
            result.Add(new SinceBootTag(tag, info.Owner, info.Description, now, firstByTag.GetValueOrDefault(tag), now - firstByTag.GetValueOrDefault(tag), perHourList));
        }
        return result;
    }

    public static string ToMarkdown(List<SinceBootTag> tags, DateTime? bootUtc, DateTime firstMinuteUtc, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Since boot: what grew, and what drove it").AppendLine();
        sb.AppendLine($"- Booted: {(bootUtc is { } b ? Fmt.Stamp(b) : "unknown")}; history from {Fmt.Stamp(firstMinuteUtc)} to {Fmt.Stamp(nowUtc)} (one-minute averages).");
        sb.AppendLine("- \"Now\" is the tag's current usage, nearly all of it allocated since boot. \"Observed\" is the change while PoolWatch was recording.");
        sb.AppendLine("- Each hour's growth is split across the processes doing the most I/O in that hour: activity correlation, not proof.").AppendLine();
        sb.AppendLine("| Tag | Owner | Description | Now | Observed |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var t in tags)
            sb.AppendLine($"| `{t.Tag.ToDisplay()}` | {t.Owner} | {t.Description.Replace("|", "\\|")} | {Fmt.Bytes(t.BytesNow)} | {Fmt.SignedBytes(t.GrowthObserved)} |");
        sb.AppendLine();
        foreach (var t in tags.Where(t => t.Hours.Any(x => x.Growth > 0)))
        {
            sb.AppendLine($"## `{t.Tag.ToDisplay()}` hour by hour").AppendLine();
            foreach (var hr in t.Hours.Where(x => x.Growth != 0))
            {
                string who = hr.Attribution.Count == 0 ? "" : ": " + string.Join(", ", hr.Attribution.Select(a => $"{a.Process} (PID {a.Pid}) {a.Share:P0} ~{Fmt.Bytes(a.Bytes)}"));
                sb.AppendLine($"- {hr.HourUtc.ToLocalTime():HH:00} {Fmt.SignedBytes(hr.Growth)}{who}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static long Bytes(History.MinutePoint m, PoolTag tag)
    {
        int i = Array.IndexOf(m.Tags, tag);
        return i >= 0 ? m.TagBytes[i] : 0;
    }
}
