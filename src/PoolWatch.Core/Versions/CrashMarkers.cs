using System.Globalization;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Versions;

public enum CrashKind { BugCheck, KernelPower, BlueScreenReport, AppCrash, AppCrashReport }

/// <summary>One crash-related event-log record, reduced to the fields that matter.</summary>
public sealed record CrashRecord(DateTime TimeUtc, CrashKind Kind, string? App, string? Code, string? Bucket, string Text);

/// <summary>
/// Crash markers read at start from the event logs, since the previous run: BugCheck (System 1001, the bugcheck code and
/// dump), Kernel-Power 41 (an unexpected restart, with its bugcheck code when there was one), the WER BlueScreen report
/// (Application 1001: the fault bucket), and Application Error 1000 / WER APPCRASH 1001 for the watched apps. A system
/// crash becomes an alert finding with a report folder (one per crash: records within 15 minutes are combined).
/// </summary>
public static class CrashMarkers
{
    public const string BugCheckProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    public const string AppErrorProvider = "Application Error";
    public const string WerProvider = "Windows Error Reporting";

    /// <summary>Apps whose crashes are reported (image names without .exe, case-insensitive).</summary>
    public static readonly IReadOnlyList<string> DefaultWatchedApps =
        ["ChatGPT", "Codex", "claude", "Claude", "node", "AI Code Conductor", "StreamDeck", "Code", "WindowsTerminal", "explorer", "dwm"];

    /// <summary>The logs read and, per log, the records of interest (providers and event IDs). The system crash records
    /// (BugCheck, Kernel-Power) have a log of their own, so app records can never crowd them out.</summary>
    public static readonly IReadOnlyList<(string channel, string filter)> Logs =
    [
        ("System", $"(Provider[@Name='{BugCheckProvider}'] and EventID=1001) or (Provider[@Name='{KernelPowerProvider}'] and EventID=41)"),
        ("Application", $"(Provider[@Name='{AppErrorProvider}'] and EventID=1000) or (Provider[@Name='{WerProvider}'] and EventID=1001)"),
    ];

    /// <summary>The XPath queries (channel, query) for records after <paramref name="sinceUtc"/>.</summary>
    public static IReadOnlyList<(string channel, string xpath)> Queries(DateTime sinceUtc, DateTime nowUtc) =>
        Logs.Select(l => (l.channel, XPath(l.filter, sinceUtc, nowUtc, null))).ToList();

    /// <summary>
    /// One log's query: the records after <paramref name="afterRecord"/> (EventRecordID, read oldest first: paging, so none
    /// is skipped) when the cursor has one, else those since <paramref name="sinceUtc"/> (C6, <see cref="CrashCursor"/>).
    /// </summary>
    public static string XPath(string filter, DateTime sinceUtc, DateTime nowUtc, long? afterRecord)
    {
        if (afterRecord is { } id) return $"*[System[{filter}] and System[EventRecordID > {id.ToString(CultureInfo.InvariantCulture)}]]";
        long ms = Math.Max(1, (long)(nowUtc - sinceUtc).TotalMilliseconds);
        return $"*[System[{filter}] and System[TimeCreated[timediff(@SystemTime) <= {ms.ToString(CultureInfo.InvariantCulture)}]]]";
    }

    /// <summary>Null for a record that is not a crash of interest (another app's crash, another WER event).</summary>
    public static CrashRecord? Parse(LogEvent e, IReadOnlyCollection<string> watchedApps)
    {
        string P(int i) => i < e.Properties.Count ? e.Properties[i] : "";
        bool Watched(string app)
        {
            string a = Path.GetFileNameWithoutExtension(app.Trim());
            return a.Length > 0 && watchedApps.Any(w => a.Equals(w, StringComparison.OrdinalIgnoreCase));
        }
        if (e.Provider.Equals(BugCheckProvider, StringComparison.OrdinalIgnoreCase) && e.Id == 1001)
        {
            string code = P(0).Split(' ', 2)[0];
            return new CrashRecord(e.TimeUtc, CrashKind.BugCheck, null, code.Length > 0 ? code : null, null,
                $"bugcheck {P(0)}" + (P(1).Length > 0 ? $"; dump {P(1)}" : "") + (P(2).Length > 0 ? $"; report {P(2)}" : ""));
        }
        if (e.Provider.Equals(KernelPowerProvider, StringComparison.OrdinalIgnoreCase) && e.Id == 41)
        {
            string? code = long.TryParse(P(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out long c) && c != 0 ? $"0x{c:x8}" : null;
            return new CrashRecord(e.TimeUtc, CrashKind.KernelPower, null, code, null,
                code is null ? "unexpected restart without a bugcheck (power loss, hard hang or a forced power-off)" : $"unexpected restart after bugcheck {code}");
        }
        if (e.Provider.Equals(WerProvider, StringComparison.OrdinalIgnoreCase) && e.Id == 1001)
        {
            // [0] bucket, [1] bucket type, [2] event name, ... [5] P1 (the app for APPCRASH).
            if (P(2).Equals("BlueScreen", StringComparison.OrdinalIgnoreCase))
                return new CrashRecord(e.TimeUtc, CrashKind.BlueScreenReport, null, null, P(0), $"WER BlueScreen report, fault bucket {P(0)}");
            if (P(2).Equals("APPCRASH", StringComparison.OrdinalIgnoreCase) && Watched(P(5)))
                return new CrashRecord(e.TimeUtc, CrashKind.AppCrashReport, P(5), null, P(0), $"{P(5)} crash report, fault bucket {P(0)}");
            return null;
        }
        if (e.Provider.Equals(AppErrorProvider, StringComparison.OrdinalIgnoreCase) && e.Id == 1000 && Watched(P(0)))
        {
            // [0] app, [1] app version, [3] faulting module, [4] module version, [6] exception code, [7] offset.
            return new CrashRecord(e.TimeUtc, CrashKind.AppCrash, P(0), P(6).Length > 0 ? "0x" + P(6).TrimStart('0', 'x').PadLeft(8, '0') : null, null,
                $"{P(0)} {P(1)} crashed in {P(3)} {P(4)}" + (P(6).Length > 0 ? $", exception 0x{P(6).TrimStart('0', 'x').PadLeft(8, '0')}" : ""));
        }
        return null;
    }

    /// <summary>A system crash: the records of one restart combined (bugcheck code, bucket, Kernel-Power).</summary>
    public sealed record SystemCrash(DateTime TimeUtc, string? Code, string? Bucket, List<CrashRecord> Records)
    {
        public string Title => Code is { } c
            ? $"System crash (bugcheck {c}{(Bucket is { } b ? $", bucket {b}" : "")}) at {Detection.Fmt.Stamp(TimeUtc)}"
            : $"Unexpected restart at {Detection.Fmt.Stamp(TimeUtc)} (no bugcheck recorded)";
    }

    /// <summary>Groups system records within 15 minutes into crashes, oldest first.</summary>
    public static List<SystemCrash> SystemCrashes(IEnumerable<CrashRecord> records)
    {
        var crashes = new List<SystemCrash>();
        foreach (var r in records.Where(r => r.Kind is CrashKind.BugCheck or CrashKind.KernelPower or CrashKind.BlueScreenReport).OrderBy(r => r.TimeUtc))
        {
            var last = crashes.Count > 0 ? crashes[^1] : null;
            if (last is not null && r.TimeUtc - last.Records[^1].TimeUtc <= TimeSpan.FromMinutes(15))
            {
                last.Records.Add(r);
                crashes[^1] = last with
                {
                    Code = last.Records.FirstOrDefault(x => x.Kind == CrashKind.BugCheck)?.Code ?? last.Code ?? r.Code,
                    Bucket = last.Bucket ?? r.Bucket,
                };
            }
            else crashes.Add(new SystemCrash(r.TimeUtc, r.Code, r.Bucket, [r]));
        }
        return crashes;
    }

    public static Finding ToFinding(SystemCrash c) => new()
    {
        TimeUtc = c.TimeUtc, Kind = FindingKind.Crash, Severity = c.Code is null ? FindingSeverity.Warning : FindingSeverity.Alert, Title = c.Title,
        Details = c.Records.Select(r => $"{Detection.Fmt.Stamp(r.TimeUtc)}: {r.Text}").ToList(),
        Data = new Dictionary<string, object?> { ["bugcheck"] = c.Code, ["bucket"] = c.Bucket, ["records"] = c.Records.Select(r => r.Kind.ToString()).ToList() },
    };

    /// <summary>App crash findings per check: the newest <paramref name="maxEach"/> one by one; older ones (a backlog paged
    /// in at once) in one summary finding with every record's line, so none is dropped (C6).</summary>
    public static List<Finding> AppFindings(IReadOnlyList<CrashRecord> apps, int maxEach = 50)
    {
        var ordered = apps.OrderBy(a => a.TimeUtc).ToList();
        var result = new List<Finding>();
        int older = Math.Max(0, ordered.Count - maxEach);
        if (older > 0)
        {
            var old = ordered.Take(older).ToList();
            var perApp = old.GroupBy(a => a.App ?? "?", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            result.Add(new Finding
            {
                TimeUtc = old[^1].TimeUtc, Kind = FindingKind.Crash, Severity = FindingSeverity.Warning,
                Title = $"App crashes: {older} older ones from {Detection.Fmt.Stamp(old[0].TimeUtc)} to {Detection.Fmt.Stamp(old[^1].TimeUtc)} " +
                        $"({string.Join(", ", perApp.Take(5).Select(g => $"{g.Key} ×{g.Count()}"))})",
                Details = old.Select(a => $"{Detection.Fmt.Stamp(a.TimeUtc)}: {a.Text}").ToList(),
                Data = new Dictionary<string, object?> { ["count"] = older, ["apps"] = perApp.ToDictionary(g => g.Key, g => (object?)g.Count()) },
            });
        }
        result.AddRange(ordered.Skip(older).Select(ToFinding));
        return result;
    }

    public static Finding ToFinding(CrashRecord r) => new()
    {
        TimeUtc = r.TimeUtc, Kind = FindingKind.Crash, Severity = FindingSeverity.Warning, Title = "App crash: " + r.Text, Image = r.App,
        Data = new Dictionary<string, object?> { ["app"] = r.App, ["code"] = r.Code, ["bucket"] = r.Bucket, ["record"] = r.Kind.ToString() },
    };
}
