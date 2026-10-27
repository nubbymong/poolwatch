using System.Globalization;
using System.Text;

namespace PoolWatch.Core.Versions;

/// <summary>
/// Where crash-record reading stopped, per event log (C6, review Part 2). The old reader took the newest 200 records of
/// each query and then moved cache\last-seen.txt to the query time, so on a log with more than 200 matching records (the
/// Application query matches every app's Error 1000 and WER 1001 before PoolWatch filters them) the older BlueScreen and
/// watched-app crash records were skipped for good. Now each log is read oldest first, a bounded page per check, and its
/// cursor is the EventRecordID of the last record actually read: nothing past it is ever skipped, and a backlog is paged
/// through on the next checks. A log without a cursor yet (first run, or a log cleared since) is read from a time.
/// </summary>
public sealed class CrashCursor
{
    /// <summary>Records read per log per check (oldest first). More are read at the next check.</summary>
    public const int PageSize = 1000;

    private readonly Dictionary<string, (DateTime? queriedUtc, long? afterRecord)> _logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The single time an older last-seen.txt held (the end of its last query), used by logs with no own entry.</summary>
    public DateTime? LegacySinceUtc { get; private set; }

    /// <summary>The last record read in a log (its EventRecordID), if known.</summary>
    public long? AfterRecord(string log) => _logs.TryGetValue(log, out var v) ? v.afterRecord : null;

    /// <summary>What to read from <paramref name="log"/> now: records after a record ID when the cursor has one and the log
    /// was not cleared since (its newest record is not below the cursor), else records since a time.</summary>
    /// <param name="newestInLog">The log's newest EventRecordID now (any record), or null when it could not be read.</param>
    public (DateTime sinceUtc, long? afterRecord) Next(string log, DateTime nowUtc, int lookbackDays, long? newestInLog)
    {
        _logs.TryGetValue(log, out var v);
        DateTime since = v.queriedUtc ?? LegacySinceUtc ?? nowUtc.AddDays(-Math.Max(0, lookbackDays));
        if (since > nowUtc) since = nowUtc.AddDays(-Math.Max(0, lookbackDays)); // a clock moved back: the lookback
        if (v.afterRecord is { } after && !(newestInLog is { } newest && newest < after)) return (since, after);
        return (since, null); // no cursor, or the log was cleared (record IDs start again): by time
    }

    /// <summary>
    /// Records a read of <paramref name="log"/> at <paramref name="queriedUtc"/>. A full page (<paramref name="pageFull"/>)
    /// may have more behind it: the cursor is the last record read, so the next check continues there. Otherwise the log
    /// was read to its end, and the cursor is the higher of that record and the newest record seen before the read (so a
    /// log with no matching record gets a cursor too, and later reads are by record ID, with no time gaps).
    /// </summary>
    public void Advance(string log, DateTime queriedUtc, long? lastRead, long? newestBefore, bool pageFull)
    {
        _logs.TryGetValue(log, out var v);
        long? after = pageFull ? lastRead ?? v.afterRecord : Max(Max(lastRead, newestBefore), v.afterRecord is { } a && !(newestBefore is { } nb && nb < a) ? a : null);
        _logs[log] = (queriedUtc, after);
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>
    /// The cache\last-seen.txt text: the time of the last query (ticks, as older versions wrote it) on the first line, then
    /// one line per log, "log ticks recordId".
    /// </summary>
    public string Format()
    {
        var sb = new StringBuilder();
        var latest = _logs.Values.Select(v => v.queriedUtc).Where(t => t is not null).DefaultIfEmpty(LegacySinceUtc).Max();
        sb.Append((latest ?? DateTime.MinValue).Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (log, (t, after)) in _logs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"{log} {(t ?? DateTime.MinValue).Ticks} {after ?? 0}\n");
        return sb.ToString();
    }

    /// <summary>Reads a last-seen.txt (either format); anything unreadable is ignored, so the worst case is the lookback.</summary>
    public static CrashCursor Parse(string? text, DateTime nowUtc)
    {
        var c = new CrashCursor();
        if (string.IsNullOrWhiteSpace(text)) return c;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 0 && Ticks(lines[0], nowUtc) is { } legacy) c.LegacySinceUtc = legacy;
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0].Length is 0 or > 64) continue;
            DateTime? t = Ticks(parts[1], nowUtc);
            long? after = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;
            c._logs[parts[0]] = (t, after);
        }
        return c;
    }

    private static DateTime? Ticks(string s, DateTime nowUtc) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && ticks > 0 && ticks <= nowUtc.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc) : null;
}
