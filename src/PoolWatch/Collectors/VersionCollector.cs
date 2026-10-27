using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;
using PoolWatch.Core;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Versions;

namespace PoolWatch.Collectors;

/// <summary>
/// Reads the versions for <see cref="VersionMarkers"/> from explicit places only: the per-user AppModel package
/// repository key (Store package full names), the Claude Code versions folder (one listing, capped), and the file
/// version of the running AI Code Conductor exe. And the crash records for <see cref="CrashMarkers"/> (one query per log).
/// </summary>
internal static class VersionCollector
{
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static string ClaudeVersionsDir(Settings s) => s.ClaudeVersionsDir.Length > 0 ? s.ClaudeVersionsDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "claude", "versions");

    /// <param name="aiccPath">The running AICC exe, if any (from the process registry).</param>
    public static Dictionary<string, string> Read(Settings settings, string? aiccPath, Action<string> log)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
            foreach (var full in key?.GetSubKeyNames() ?? [])
                if (VersionMarkers.FromPackageFullName(full) is { } p && Version.TryParse(p.version, out var pv) &&
                    (!versions.TryGetValue(p.name, out var have) || !Version.TryParse(have, out var hv) || pv > hv))
                    versions[p.name] = p.version; // several staged versions of a package: the newest
        }
        catch (Exception ex) { log("package versions unavailable: " + ex.Message); }
        try
        {
            string dir = ClaudeVersionsDir(settings);
            if (Directory.Exists(dir) && VersionMarkers.NewestClaudeCode(Directory.EnumerateFileSystemEntries(dir).Take(500).Select(Path.GetFileName).OfType<string>()) is { } cc)
                versions["Claude Code"] = cc;
        }
        catch (Exception ex) { log("Claude Code versions unavailable: " + ex.Message); }
        try
        {
            if (aiccPath is not null && File.Exists(aiccPath) && FileVersionInfo.GetVersionInfo(aiccPath) is { } fv && (fv.ProductVersion ?? fv.FileVersion) is { Length: > 0 } v)
                versions["AI Code Conductor"] = v.Split('+')[0].Trim();
        }
        catch (Exception ex) { log("AICC version unavailable: " + ex.Message); }
        return versions;
    }

    /// <summary>
    /// C6: the crash records after each log's cursor, oldest first, at most <see cref="CrashCursor.PageSize"/> per log; the
    /// cursor moves only over records actually read (the old reader took the newest 200 and then skipped the rest for good).
    /// A log that could not be read keeps its cursor. Returns the records (oldest first) and whether a log filled its page
    /// (more to read: the caller checks again soon).
    /// </summary>
    public static (List<LogEvent> records, bool more) CrashEvents(CrashCursor cursor, DateTime queryUtc, int lookbackDays, Action<string> log)
    {
        var list = new List<LogEvent>();
        bool more = false;
        foreach (var (channel, filter) in CrashMarkers.Logs)
        {
            try
            {
                long? newest = NewestRecordId(channel); // before the read: a record written meanwhile is above the new cursor
                var (since, after) = cursor.Next(channel, queryUtc, lookbackDays, newest);
                var page = new List<LogEvent>();
                long? last = null;
                using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, CrashMarkers.XPath(filter, since, queryUtc, after))))
                    while (page.Count < CrashCursor.PageSize && reader.ReadEvent() is { } rec)
                        using (rec)
                        {
                            if (rec.RecordId is { } id && (last is null || id > last)) last = id;
                            var props = new List<string>();
                            try { foreach (var p in rec.Properties.Take(12)) props.Add(p.Value?.ToString() ?? ""); } catch { }
                            page.Add(new LogEvent((rec.TimeCreated ?? DateTime.Now).ToUniversalTime(), channel, rec.ProviderName ?? "", rec.Id, null, props));
                        }
                bool full = page.Count >= CrashCursor.PageSize;
                more |= full;
                cursor.Advance(channel, queryUtc, last, newest, full); // only once the page was read completely
                list.AddRange(page);
            }
            catch (Exception ex) { log($"crash records from {channel} unavailable: {ex.Message}"); }
        }
        list.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return (list, more);
    }

    /// <summary>The newest EventRecordID in a log (any record), or null. Tells a cleared log (IDs start again) from a quiet one.</summary>
    private static long? NewestRecordId(string channel)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, "*") { ReverseDirection = true });
            using var rec = reader.ReadEvent();
            return rec?.RecordId;
        }
        catch { return null; }
    }
}
