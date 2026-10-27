using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Spawn;

/// <summary>
/// Looks for a .git folder or file at a fixed list of places where none belongs: the user profile, each OneDrive
/// root (and the OneDrive Desktop), each fixed volume root. Explicit paths only; nothing is enumerated. A stray .git
/// in the profile made one app treat the whole profile as a repository (git status / ls-files over AppData and
/// OneDrive, tens of times a minute). Reports each one when first seen and again when it disappears.
/// </summary>
public sealed class StrayGitCheck
{
    private readonly IFileProbe _probe;
    private readonly GitRootResolver _git;
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    public StrayGitCheck(IFileProbe probe, GitRootResolver git)
    {
        _probe = probe;
        _git = git;
    }

    /// <summary>Paths checked by the last run, with the result.</summary>
    public List<(string Path, string What, bool Found)> LastRun { get; } = new();
    public DateTime? LastRunUtc { get; private set; }

    public List<Finding> Run(IEnumerable<(string Path, string What)> candidates, GitRootContext ctx, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        LastRun.Clear();
        LastRunUtc = nowUtc;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, what) in candidates)
        {
            string dir = WinPath.Normalize(path);
            if (!WinPath.IsRooted(dir) || WinPath.IsUnc(dir) || !seen.Add(dir)) continue;
            string git = dir.TrimEnd('\\') + "\\.git";
            bool found;
            try { found = _probe.IsLocalFixed(dir) && (_probe.DirectoryExists(git) || _probe.FileExists(git)); }
            catch { found = false; }
            LastRun.Add((dir, what, found));
            if (found && _present.Add(dir))
            {
                var info = _git.Classify(dir, ctx);
                findings.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.StrayGit, Severity = FindingSeverity.Warning,
                    Title = $"Stray .git at {what} ({git})",
                    Details =
                    [
                        $"Any tool that asks git for the repository root of a folder under {dir} gets {dir}: a status or ls-files there scans everything below it.",
                        info.Flags.Count > 0 ? "Root: " + string.Join(", ", info.Flags) : "Root looks like a normal repository",
                        "PoolWatch never changes it; renaming or removing it is your decision.",
                    ],
                    Data = new Dictionary<string, object?> { ["path"] = git, ["where"] = what, ["flags"] = info.Flags.ToList(), ["trackedFiles"] = info.TrackedFiles },
                });
            }
            else if (!found && _present.Remove(dir))
            {
                findings.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.StrayGit, Title = $"The .git at {what} ({git}) is gone",
                    Data = new Dictionary<string, object?> { ["path"] = git, ["where"] = what, ["removed"] = true },
                });
            }
        }
        return findings;
    }
}
