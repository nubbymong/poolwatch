using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Versions;

/// <summary>
/// App versions on the timeline: Store packages (ChatGPT, Codex: package full names from the per-user AppModel
/// repository key), the Claude Code versions installed under <c>%USERPROFILE%\.local\share\claude\versions</c> (one
/// folder listing), and the AI Code Conductor exe's file version. A change against the previous check (kept across
/// runs) is a finding. Explicit locations only, never a search.
/// </summary>
public static partial class VersionMarkers
{
    /// <summary>Store packages whose versions are tracked: package family name prefixes.</summary>
    public static readonly IReadOnlyList<(string prefix, string name)> WatchedPackages =
        [("OpenAI.ChatGPT", "ChatGPT"), ("OpenAI.Codex", "Codex"), ("Claude", "Claude"), ("Anthropic", "Claude")];

    /// <summary>"OpenAI.ChatGPT-Desktop_1.2025.112.0_x64__2p2nqsd0c76g0" to ("ChatGPT", "1.2025.112.0"); null for other packages.</summary>
    public static (string name, string version)? FromPackageFullName(string fullName)
    {
        var m = PackageFullName().Match(fullName);
        if (!m.Success) return null;
        string family = m.Groups["n"].Value;
        foreach (var (prefix, name) in WatchedPackages)
            if (family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (name, m.Groups["v"].Value);
        return null;
    }

    /// <summary>The newest version among the Claude Code version folder names ("2.0.14", "2.0.15"); null when none parse.</summary>
    public static string? NewestClaudeCode(IEnumerable<string> folderNames)
    {
        Version? best = null;
        string? bestName = null;
        foreach (var n in folderNames)
        {
            // The folder is in your profile, where any program of yours can name a folder: only a name that is a version
            // as a whole counts (no line break or '=' can reach versions.txt).
            if (Clean(n) != n) continue;
            var m = VersionPrefix().Match(n);
            if (!m.Success || !Version.TryParse(m.Value, out var v)) continue;
            if (best is null || v > best) { best = v; bestName = n; }
        }
        return bestName;
    }

    /// <summary>The names PoolWatch tracks: what <see cref="Parse"/> keeps from versions.txt.</summary>
    public static readonly IReadOnlySet<string> TrackedApps =
        new HashSet<string>(WatchedPackages.Select(p => p.name).Append(ClaudeCode).Append(Aicc), StringComparer.Ordinal);

    public const string ClaudeCode = "Claude Code", Aicc = "AI Code Conductor";

    /// <summary>
    /// A version as PoolWatch keeps it: two to four numbers ("2.1.0", "1.2025.112.0"), optionally a pre-release label of
    /// letters, digits, dots and dashes ("2.1.0-rc.9"), with build metadata after a '+' dropped. Anything else (a line
    /// break, '=', spaces, more than 64 characters) is not a version: null.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        string v = raw.Split('+')[0].Trim();
        if (v.Length is 0 or > 64) return null;
        var m = StrictVersion().Match(v);
        return m.Success && Version.TryParse(m.Groups["n"].Value, out _) ? v : null;
    }

    /// <summary>
    /// AI Code Conductor's version from its exe's version resource: only when the resource names the product (not only the
    /// file name), and only a well-formed version (<see cref="Clean"/>), the product version first; else null.
    /// </summary>
    public static string? AiccVersion(string? productName, string? productVersion, string? fileVersion) =>
        string.Equals(productName?.Trim(), Aicc, StringComparison.OrdinalIgnoreCase) ? Clean(productVersion) ?? Clean(fileVersion) : null;

    /// <summary>Changes from <paramref name="previous"/> to <paramref name="current"/> (name to version), as findings.</summary>
    public static List<Finding> Diff(IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> current, DateTime nowUtc)
    {
        var found = new List<Finding>();
        if (previous is null)
        {
            if (current.Count > 0)
                found.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.VersionChange, Title = "App versions: " + string.Join(", ", current.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")),
                    Data = current.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                });
            return found;
        }
        foreach (var (name, v) in current.OrderBy(kv => kv.Key))
        {
            if (!previous.TryGetValue(name, out var old))
                found.Add(Change(nowUtc, name, null, v, $"{name} {v} installed"));
            else if (old != v)
                found.Add(Change(nowUtc, name, old, v, $"{name} updated {old} -> {v}"));
        }
        foreach (var (name, old) in previous.OrderBy(kv => kv.Key))
            if (!current.ContainsKey(name)) found.Add(Change(nowUtc, name, old, null, $"{name} {old} no longer found"));
        return found;
    }

    private static Finding Change(DateTime t, string name, string? from, string? to, string title) => new()
    {
        TimeUtc = t, Kind = FindingKind.VersionChange, Severity = FindingSeverity.Warning, Title = "Version change: " + title,
        Data = new Dictionary<string, object?> { ["app"] = name, ["from"] = from, ["to"] = to },
    };

    /// <summary>
    /// The state file: one "name=version" per line, as <see cref="Parse"/> reads it back: only the apps PoolWatch tracks
    /// (<see cref="TrackedApps"/>, names of its own with no '=' or line break), each only with a well-formed version
    /// (<see cref="Clean"/>), so no value can add a line or another app's entry.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, string> versions) =>
        string.Concat(versions.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Where(kv => TrackedApps.Contains(kv.Key))
            .Select(kv => (name: kv.Key, version: Clean(kv.Value)))
            .Where(x => x.version is not null)
            .Select(x => $"{x.name}={x.version}\n"));

    /// <summary>The state file read back: only the apps PoolWatch tracks (<see cref="TrackedApps"/>), each with a well-formed version.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = line.IndexOf('=');
            if (i > 0 && TrackedApps.Contains(line[..i]) && Clean(line[(i + 1)..]) is { } v) d[line[..i]] = v;
        }
        return d;
    }

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+){1,3})(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?$")] private static partial Regex StrictVersion();
    [GeneratedRegex(@"^(?<n>[^_]+)_(?<v>\d+(?:\.\d+){1,3})_")] private static partial Regex PackageFullName();
    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}")] private static partial Regex VersionPrefix();
}
