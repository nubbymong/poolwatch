using System.Globalization;
using System.IO.Compression;
using System.Text;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Core.Boot;

/// <summary>
/// "Export vendor bundle" (item 13): one zip in the shape the public report asked maintainers to request: app
/// packages and the Windows build; the clean-boot baseline; timestamped samples (commit, paged / non-paged, handles,
/// process counts); tag snapshots before launch, during growth, after exit and after reboot; the filter list and
/// container state; git roots and the spawn trace; crash metadata. Every file is text and is redacted by default
/// (user, machine, home paths, SID and common secret patterns: <see cref="Redactor"/>).
/// </summary>
public static class VendorBundle
{
    /// <summary>What each part is, in the order the README lists them (file name, description).</summary>
    public static readonly IReadOnlyList<(string file, string what)> Parts =
    [
        ("AGENTS.md", "a guide for an AI assistant you ask for help: what each file holds and how to read it (added as written, not redacted: it says nothing about this PC)"),
        ("system.json", "Windows edition and build, PoolWatch version, uptime, RAM, app package / Claude Code / AICC versions"),
        ("boot-baseline.json", "the first full tag snapshot after this boot (and how long after boot it was taken)"),
        ("boots.md", "every boot PoolWatch saw: peak paged / non-paged, commit, top tags at the peak, hours it ran"),
        ("samples.csv", "one row per minute: commit, commit limit, paged / non-paged, system handles, process count"),
        ("tags/", "tag snapshots: after-reboot (boot baseline), history-start, now, during-growth at incident peaks, before-launch-<app> / after-exit-<app> " +
                  "for each watched app (the last snapshot before it started, the first after it exited), and before-launch / app-running / after-exit when an experiment ran"),
        ("incidents.md", "each pool-growth incident with its top tag, growth and suspects (during growth)"),
        ("experiment/", "the guided A/B experiment's report and per-tag phase changes, when one ran"),
        ("filters.json", "minifilter instances per volume, watched services (bindflt, wcifs, vmcompute, CoworkVMService) and filter / package events"),
        ("git-and-spawns.md", "git root findings (stray .git at a profile / OneDrive / volume root) and spawn storms"),
        ("spawns.csv", "the most recent spawn-storm trace (process starts with command lines), when there was one"),
        ("crashes.md", "bugchecks, unexpected restarts and watched-app crashes from the event logs"),
        ("findings.jsonl", "every diagnostic finding of this run"),
    ];

    public static string Readme(bool redacted, DateTime nowUtc, string poolWatchVersion)
    {
        var sb = new StringBuilder("# PoolWatch vendor bundle\n\n");
        sb.AppendLine($"- Made {Detection.Fmt.Stamp(nowUtc)} by PoolWatch {poolWatchVersion}. Read-only data from this PC; nothing was sent anywhere.");
        sb.AppendLine("- Asking an AI assistant to help read it? Point it at `AGENTS.md` first.");
        sb.AppendLine(redacted
            ? "- Redacted: user name, machine name, home folder and TEMP folder paths, domain and SID are replaced by <USER>, <MACHINE>, <HOME>, <TEMP>, <DOMAIN>, <SID>, " +
              "and values that look like secrets (a password, token or API key on a command line) by <SECRET>. Redaction is best-effort: read the files before you post them."
            : "- NOT redacted: user, machine and folder names and full command lines are as recorded, and may hold secrets. Do not post this bundle publicly.");
        sb.AppendLine().AppendLine("| File | What it is |").AppendLine("|---|---|");
        foreach (var (file, what) in Parts) sb.AppendLine($"| `{file}` | {what} |");
        return sb.ToString();
    }

    /// <summary>
    /// C6: the bundle entry for a tag's table at an incident's peak. Raw tag names collided on case-only differences (Reff
    /// and RefF are one file once extracted on Windows) and broke on characters Windows forbids; the name is now the
    /// readable tag with anything but letters and digits replaced, plus the hex of its 4 raw bytes
    /// (<see cref="PoolTag.ToFileSafe"/>), and the peak's local date and minute.
    /// </summary>
    public static string DuringGrowthEntry(PoolTag tag, DateTime peakUtc) =>
        $"tags/during-growth-{tag.ToFileSafe()}-{peakUtc.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// A zip entry name that extracts on Windows: every path segment without the characters Windows forbids (&lt;&gt;:"|?* and
    /// control characters), without trailing dots / spaces and not a reserved device name; and unique among
    /// <paramref name="used"/> ignoring case (a later clash gets "-2", "-3" ... before its extension).
    /// </summary>
    public static string EntryName(string path, ISet<string> used)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment).ToList();
        string name = segments.Count == 0 ? "_" : string.Join('/', segments);
        if (used.Add(name)) return name;
        int slash = name.LastIndexOf('/'), dot = name.LastIndexOf('.');
        string stem = dot > slash + 1 ? name[..dot] : name, ext = dot > slash + 1 ? name[dot..] : "";
        for (int i = 2; ; i++)
            if (used.Add($"{stem}-{i}{ext}")) return $"{stem}-{i}{ext}";
    }

    private static string SafeSegment(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        string seg = sb.ToString().TrimEnd('.', ' ');
        if (seg.Length == 0 || seg == "..") return "_";
        string stem = seg.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + seg : seg;
    }

    /// <summary>tag,paged_bytes,nonpaged_bytes, biggest first.</summary>
    public static string TagCsv(IEnumerable<TagSize> tags)
    {
        var sb = new StringBuilder("tag,paged_bytes,nonpaged_bytes,total_bytes\n");
        foreach (var t in tags.OrderByDescending(t => t.Total))
            sb.Append(CultureInfo.InvariantCulture, $"{Util.Csv.Escape(t.Tag)},{t.Paged},{t.NonPaged},{t.Total}\n");
        return sb.ToString();
    }

    /// <summary>The one-minute history as samples.csv.</summary>
    public static string SamplesCsv(History.History h)
    {
        var sb = new StringBuilder("time_utc,commit_bytes,commit_limit_bytes,available_bytes,paged_bytes,nonpaged_bytes,system_handles,processes\n");
        foreach (var m in h.Minutes)
            sb.Append(CultureInfo.InvariantCulture,
                $"{m.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)},{m.System.CommitBytes},{m.System.CommitLimitBytes},{m.System.AvailableBytes},{m.Totals.CommittedPaged},{m.Totals.CommittedNonPaged},{m.System.Handles},{m.System.Processes}\n");
        return sb.ToString();
    }

    /// <summary>A minute's tag table from the one-minute history (paged + non-paged together, as that history keeps them).</summary>
    public static string MinuteTagCsv(History.MinutePoint m)
    {
        var sb = new StringBuilder("# one-minute average at " + m.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\ntag,total_bytes\n");
        foreach (var (t, b) in m.Tags.Zip(m.TagBytes).OrderByDescending(x => x.Second))
            sb.Append(CultureInfo.InvariantCulture, $"{Util.Csv.Escape(t.ToDisplay())},{b}\n");
        return sb.ToString();
    }

    /// <summary>Writes the zip (text files only; redacted unless <paramref name="redaction"/> is null). Returns its path.</summary>
    /// <param name="extraFiles">Text files added as they are, never redacted (the guide for AI assistants), as in
    /// <see cref="Exporter.Export"/>.</param>
    public static string Write(string exportsDir, DateTime nowUtc, IReadOnlyDictionary<string, string> files, RedactionInfo? redaction, Func<string, string> checkWrite,
        IReadOnlyDictionary<string, string>? extraFiles = null)
    {
        Directory.CreateDirectory(checkWrite(exportsDir));
        string name = $"vendor-bundle-{nowUtc.ToLocalTime():yyyyMMdd-HHmmss}{(redaction is not null ? "-redacted" : "")}.zip";
        string zipPath = checkWrite(Path.Combine(exportsDir, name));
        string tmp = checkWrite(zipPath + ".tmp");
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var (path, content) in extraFiles ?? new Dictionary<string, string>())
                    Add(zip, EntryName(path, used), content);
                foreach (var (path, content) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    Add(zip, EntryName(path, used), redaction is null ? content : Exporter.RedactText(content, redaction, Exporter.IsStructured(path)));
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return zipPath;
    }

    private static void Add(ZipArchive zip, string entryName, string content)
    {
        using var s = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        s.Write(new UTF8Encoding(false).GetBytes(content));
    }
}
