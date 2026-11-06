using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Versions;

namespace PoolWatch.Collectors;

/// <summary>
/// Reads the versions for <see cref="VersionMarkers"/> from explicit places only: the per-user AppModel package
/// repository key (Store package full names), the Claude Code versions folder (one listing, capped), and the file
/// version of the running AI Code Conductor exe (only one that is AICC's own: installed for everyone under Program Files, or
/// signed by the same publisher as PoolWatch; and its version resource must name the product). And the crash records
/// for <see cref="CrashMarkers"/> (one query per log).
/// </summary>
internal static class VersionCollector
{
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static string ClaudeVersionsDir(Settings s) => s.ClaudeVersionsDir.Length > 0 ? s.ClaudeVersionsDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "claude", "versions");

    /// <param name="aiccPaths">Every running exe named like AICC's (from the process registry): the first that is AICC's own gives its version.</param>
    public static Dictionary<string, string> Read(Settings settings, IEnumerable<string?> aiccPaths, Action<string> log)
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
            // Any program can be called "AI Code Conductor.exe" and copy its version resource: only an exe that is AICC's own
            // gives the version (IsAiccsOwn), and only a well-formed one (VersionMarkers.AiccVersion).
            if (AiccVersion(aiccPaths, p => TrustedAiccVersion(p, ThisPcsReads.Value, Verdicts)) is { } v) versions[VersionMarkers.Aicc] = v;
        }
        catch (Exception ex) { log("AICC version unavailable: " + ex.Message); }
        finally { Verdicts.EndCheck(); }
        return versions;
    }

    /// <summary>
    /// The version <paramref name="versionIfOwn"/> gives for the first of <paramref name="candidates"/> (each path once)
    /// that is AICC's own; null when none is. A same-named program listed first neither hides AICC's version nor sets it.
    /// </summary>
    internal static string? AiccVersion(IEnumerable<string?> candidates, Func<string, string?> versionIfOwn)
    {
        foreach (var path in candidates.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (versionIfOwn(path) is { } v) return v;
        return null;
    }

    /// <summary>
    /// Pure: whether an exe is AI Code Conductor's own. Either only administrators can change it (an install for everyone,
    /// under Program Files: every folder up to there and its own rules, <see cref="Engine.InstallGuard.UserWritable"/>),
    /// or it is signed by the same publisher as this PoolWatch, through the same certificate authority (<see
    /// cref="FileSignature.SamePublisher"/>). <paramref name="signature"/> is read only then, and only when this PoolWatch
    /// has a signature any could match (<see cref="FileSignature.CanBeMatched"/>): never for an unsigned development
    /// build, where the answer can only be no. No publisher is named here.
    /// </summary>
    internal static bool IsAiccsOwn(bool adminOnly, Func<SignatureInfo?> signature, SignatureInfo? poolWatchsOwn) =>
        adminOnly || (FileSignature.CanBeMatched(poolWatchsOwn) && FileSignature.SamePublisher(signature(), poolWatchsOwn));

    /// <summary>This PoolWatch's own signature, read once.</summary>
    private static readonly Lazy<SignatureInfo?> OwnSignature = new(() => Environment.ProcessPath is { } exe ? FileSignature.Verify(exe) : null);

    /// <summary>An exe as held for <see cref="TrustedAiccVersion"/>: its final path and what tells its content from another's.</summary>
    internal sealed record AiccFile(string Final, Engine.NoFollow.FileId Id, DateTime LastWriteUtc, long Length);

    /// <summary>
    /// The reads <see cref="TrustedAiccVersion"/> makes, one step each, so tests can pin every step without a real exe.
    /// <see cref="Hold"/> opens the exe sharing only reads and gives the hold (released when disposed) and the file as it
    /// is held (null when its final path cannot be told), or throws when it cannot be opened; <see cref="AdminOnly"/>,
    /// <see cref="Signature"/> and <see cref="VersionResource"/> read the file at that final path.
    /// </summary>
    internal sealed record AiccReads(Func<string, (IDisposable Hold, AiccFile? File)> Hold, Func<string, bool> AdminOnly,
        Func<string, SignatureInfo?> Signature, Func<SignatureInfo?> Own, Func<string, (string? Product, string? ProductVersion, string? FileVersion)> VersionResource);

    /// <summary>This PC's reads (<see cref="ThisPcsReads"/>).</summary>
    internal static AiccReads ThisPc => ThisPcsReads.Value;

    /// <summary>This PC's reads: the real hold, admin-only check (<see cref="Engine.AdminPlaces"/>), signature and version resource.</summary>
    private static readonly Lazy<AiccReads> ThisPcsReads = new(() => new AiccReads(
        path =>
        {
            var hold = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (Engine.NoFollow.FinalPath(hold) is not { } final) return (hold, null);
                var q = Engine.NoFollow.Query(hold);
                return (hold, new AiccFile(final, q.Id, q.LastWriteUtc, q.Length));
            }
            catch { hold.Dispose(); throw; }
        },
        final => Engine.AdminPlaces.OfThisPc().UserWritable(final) is null,
        FileSignature.Verify,
        () => OwnSignature.Value,
        final => { var fv = FileVersionInfo.GetVersionInfo(final); return (fv.ProductName, fv.ProductVersion, fv.FileVersion); }));

    /// <summary>The running same-named exes found not to be AICC's own by their signature (<see cref="SignatureVerdicts"/>).</summary>
    private static readonly SignatureVerdicts Verdicts = new();

    /// <summary>
    /// The version of the exe at <paramref name="path"/> when it is AICC's own (<see cref="IsAiccsOwn"/>), else null (and
    /// when it cannot be read). It is held open meanwhile, sharing only reads, so it cannot be written, renamed or deleted
    /// between the check and the read, and everything is read at its final path (no link on the way). A file whose
    /// signature was found not to be PoolWatch's publisher's is not checked again while it is unchanged (<paramref
    /// name="verdicts"/>): the full-file signature check runs once per such file, however often the versions are read.
    /// </summary>
    internal static string? TrustedAiccVersion(string path, AiccReads reads, SignatureVerdicts verdicts)
    {
        try
        {
            var (hold, file) = reads.Hold(path);
            using (hold)
            {
                if (file is null) return null;
                bool adminOnly = reads.AdminOnly(file.Final);
                if (!adminOnly && verdicts.KnownNotOwn(file)) return null;
                if (!IsAiccsOwn(adminOnly, () => reads.Signature(file.Final), reads.Own()))
                {
                    verdicts.NotOwn(file);
                    return null;
                }
                var (product, productVersion, fileVersion) = reads.VersionResource(file.Final);
                return VersionMarkers.AiccVersion(product, productVersion, fileVersion);
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The same-named exes whose signature was found not to be AICC's own, each by its final path, file ID, last write time
    /// and size: one that changes in any of them is checked again. Only a "no" is kept, never a "yes" (an exe that passes is
    /// checked again at every read, so content changed behind an unchanged time is never trusted), and only for the exes
    /// met in the last read (<see cref="EndCheck"/>), so it holds no more than the processes running then.
    /// </summary>
    internal sealed class SignatureVerdicts
    {
        private readonly object _lock = new();
        private HashSet<AiccFile> _notOwn = new(), _seen = new();

        /// <summary>Whether this file was found not to be AICC's own and has not changed since.</summary>
        public bool KnownNotOwn(AiccFile file)
        {
            lock (_lock)
            {
                if (!_notOwn.Contains(file)) return false;
                _seen.Add(file);
                return true;
            }
        }

        /// <summary>Records that this file is not AICC's own.</summary>
        public void NotOwn(AiccFile file)
        {
            lock (_lock) { _notOwn.Add(file); _seen.Add(file); }
        }

        /// <summary>After a read of the versions: forgets every file that was not met in it.</summary>
        public void EndCheck()
        {
            lock (_lock) { _notOwn = _seen; _seen = new(); }
        }

        /// <summary>How many files are known not to be AICC's own (for tests).</summary>
        public int Count { get { lock (_lock) return _notOwn.Count; } }
    }

    /// <summary>
    /// The crash records after each log's cursor, oldest first, at most <see cref="CrashCursor.PageSize"/> per log; the
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
