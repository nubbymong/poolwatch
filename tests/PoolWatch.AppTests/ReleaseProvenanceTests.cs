using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The release's provenance, which ci.yml's provenance job attests on a v* tag only, so that no pull request runs it:
/// the job covers exactly the files the publish job puts on the release, by the hashes the sign job takes where it makes
/// them; it alone holds the rights it needs, the workflow's own token stays read-only, and every action of every
/// workflow stays pinned to a commit (AGENTS.md, "The release path").
/// </summary>
public sealed class ReleaseProvenanceTests
{
    private const string TagOnly = "startsWith(github.ref, 'refs/tags/v')";

    private static string WorkflowsFolder() => Path.Combine(TrayIconsTests.RepoRoot(), ".github", "workflows");

    private static string Ci() => File.ReadAllText(Path.Combine(WorkflowsFolder(), "ci.yml")).Replace("\r\n", "\n");

    /// <summary>
    /// ci.yml's jobs by name, each with its lines: from its header (a line "  &lt;name&gt;:" under jobs:) up to the next
    /// line indented less than four spaces that is not blank and not a comment.
    /// </summary>
    private static Dictionary<string, string> Jobs(string ci)
    {
        int at = ci.IndexOf("\njobs:\n", StringComparison.Ordinal);
        Assert.True(at > 0, "ci.yml has no jobs");
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match job in Regex.Matches(ci[at..], @"(?m)^  (?<name>[\w-]+):\n(?<body>(?:(?:    .*|  #.*|)\n)*)"))
            jobs[job.Groups["name"].Value] = job.Groups["body"].Value;
        foreach (string name in new[] { "sign", "publish", "provenance" }) Assert.Contains(name, jobs.Keys);
        return jobs;
    }

    /// <summary>The value of a key of the job itself (a line "    &lt;key&gt;: ..."), or null when the job has none.</summary>
    private static string? Key(string job, string key)
    {
        var line = Regex.Match(job, $@"(?m)^    {Regex.Escape(key)}:(?<v>.*)$");
        return line.Success ? line.Groups["v"].Value.Trim() : null;
    }

    /// <summary>
    /// The lines right under a key of the job itself (indented six spaces), as "key: value" in order, comments left out;
    /// none when the job has no such key.
    /// </summary>
    private static List<string> Under(string job, string key)
    {
        var block = Regex.Match(job, $@"(?m)^    {Regex.Escape(key)}:\n(?<lines>(?:      .*\n)+)");
        if (!block.Success) return [];
        return block.Groups["lines"].Value.Split('\n')
            .Select(l => Regex.Replace(l, @"(^|\s+)#.*$", "").Trim())
            .Where(l => l.Length > 0)
            .Select(l => Regex.Replace(l, @":\s+", ": "))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The file names a line of <paramref name="job"/> lists (the "v" group of <paramref name="pattern"/>), quoted or not,
    /// in order, with the tag's variable <paramref name="tag"/> read as TAG.
    /// </summary>
    private static List<string> Listed(string job, string pattern, string tag)
    {
        var line = Regex.Match(job, pattern);
        Assert.True(line.Success, $"no line of the job matches {pattern}");
        return Regex.Matches(line.Groups["v"].Value, @"""(?<n>[^""]+)""|'(?<n>[^']+)'|(?<n>[^\s""',]+)")
            .Select(m => m.Groups["n"].Value.Replace(tag, "TAG", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// The provenance job runs on a v* tag only, once the release is published, as sign and publish do. It attests the
    /// files the publish job puts on the release, by the hashes the sign job takes of them where it makes them (after it
    /// writes SHA256SUMS.txt, before it uploads them) and passes on as a job output: the publish job's list, the sign job's
    /// and the provenance job's two name the same files in the same order. The job attaches the attestation to the release
    /// under the name Scorecard's Signed-Releases check looks for (.intoto.jsonl), then checks each file on the release
    /// against that copy: signed by ci.yml, for the tag and its commit.
    /// </summary>
    [Fact]
    public void TheProvenanceCoversExactlyTheFilesThePublishJobPutsOnTheRelease()
    {
        var jobs = Jobs(Ci());
        string sign = jobs["sign"], publish = jobs["publish"], provenance = jobs["provenance"];
        Assert.Equal(TagOnly, Key(sign, "if"));
        Assert.Equal(TagOnly, Key(publish, "if"));
        Assert.Equal(TagOnly, Key(provenance, "if"));
        Assert.Equal(["publish", "sign"], (Key(provenance, "needs") ?? "").Trim('[', ']').Split(',').Select(n => n.Trim()).Order(StringComparer.Ordinal));

        var published = Listed(publish, @"\n *files=""(?<v>[^""]+)""\n", "$GITHUB_REF_NAME");
        Assert.Contains("PoolWatch-TAG-setup-win-x64.exe", published);
        Assert.Contains("SHA256SUMS.txt", published);
        Assert.Equal(published, Listed(sign, @"\n *\$names = (?<v>.+)\n", "$tag"));
        Assert.Equal(published, Listed(provenance, @"\n *expected=""(?<v>[^""]+)""\n", "$GITHUB_REF_NAME"));
        Assert.Equal(published, Listed(provenance, @"\n *for f in (?<v>.+); do\n", "$GITHUB_REF_NAME"));

        // The sign job hashes the files once they are all made, and passes the hashes on.
        int sums = sign.IndexOf("[IO.File]::WriteAllText(\"$PWD/release/SHA256SUMS.txt\"", StringComparison.Ordinal);
        int hash = sign.IndexOf("\n      - name: Hash the release files for their provenance\n        id: subjects\n", StringComparison.Ordinal);
        int upload = sign.IndexOf("\n      - name: Upload the signed release files\n", StringComparison.Ordinal);
        Assert.True(sums > 0 && hash > sums && upload > hash, "the sign job does not hash the release files after it writes SHA256SUMS.txt and before it uploads them");
        Assert.Contains("\"hashes=$([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)))\" >> $env:GITHUB_OUTPUT", sign);
        Assert.Equal(["hashes: ${{ steps.subjects.outputs.hashes }}"], Under(sign, "outputs"));

        // The provenance job attests them by those hashes, with GitHub's attest action pinned to a commit.
        Assert.Contains("\n          HASHES: ${{ needs.sign.outputs.hashes }}\n", provenance);
        Assert.Contains("printf '%s' \"$HASHES\" | base64 -d > \"$RUNNER_TEMP/subjects.sha256\"", provenance);
        Assert.Matches(@"\n        uses: actions/attest@[0-9a-f]{40} # v[0-9]", provenance);
        Assert.Contains("\n          subject-checksums: ${{ runner.temp }}/subjects.sha256\n", provenance);

        // It attaches the attestation as an .intoto.jsonl file and checks the release's files against that copy.
        Assert.Contains("\n          name=\"PoolWatch-$GITHUB_REF_NAME.intoto.jsonl\"\n", provenance);
        Assert.Contains("gh release upload \"$GITHUB_REF_NAME\" \"$RUNNER_TEMP/$name\" --repo \"$GITHUB_REPOSITORY\" --clobber", provenance);
        Assert.Contains("gh release download \"$GITHUB_REF_NAME\" --repo \"$GITHUB_REPOSITORY\"", provenance);
        Assert.Contains("gh attestation verify \"$f\" --bundle \"$name\" --repo \"$GITHUB_REPOSITORY\"", provenance);
        Assert.Contains("--signer-workflow \"$GITHUB_REPOSITORY/.github/workflows/ci.yml\" --source-ref \"$GITHUB_REF\" --source-digest \"$GITHUB_SHA\"", provenance);
    }

    /// <summary>
    /// The workflow's own token is read-only, and its triggers are the four it had (no pull_request_target, so a fork's
    /// pull request never runs with a write token or a secret). Of the jobs, only the provenance job may get the run's
    /// identity token (to sign) and keep an attestation, and only it and the publish job may write the repository's
    /// contents; the provenance job asks for nothing more.
    /// </summary>
    [Fact]
    public void OnlyTheProvenanceJobHoldsItsRightsAndTheWorkflowsOwnStayReadOnly()
    {
        string ci = Ci();
        Assert.Contains("\non:\n  push:\n    branches: [main]\n    tags: ['v*']\n  pull_request:\n  workflow_dispatch:\n\npermissions:\n  contents: read\n\n", ci);
        var jobs = Jobs(ci);
        Assert.Equal(["attestations: write", "contents: write", "id-token: write"], Under(jobs["provenance"], "permissions"));
        foreach (var (name, job) in jobs)
        {
            string[] allowed = name switch
            {
                "provenance" => ["attestations: write", "contents: write", "id-token: write"],
                "publish" => ["contents: write"],
                _ => ["contents: read"],
            };
            var more = Under(job, "permissions").Where(r => !allowed.Contains(r)).ToList();
            Assert.True(more.Count == 0, $"the job {name} asks for {string.Join(", ", more)}");
        }
    }

    /// <summary>
    /// Every action and reusable workflow that a workflow uses is pinned to a full commit (40 hex digits), never to a tag
    /// or a branch, which its repository can move; a step of this repository's own (a path from ./) aside.
    /// </summary>
    [Fact]
    public void EveryActionOfEveryWorkflowIsPinnedToACommit()
    {
        var files = Directory.GetFiles(WorkflowsFolder())
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == "ci.yml");
        var uses = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?m)^[ \t-]*uses:[ \t]*(?<u>[^\s#]+)")
            .Select(m => (File: Path.GetFileName(f), Uses: m.Groups["u"].Value))).ToList();
        Assert.Contains(uses, u => u.Uses.StartsWith("actions/attest@", StringComparison.Ordinal));
        var unpinned = uses.Where(u => !u.Uses.StartsWith("./", StringComparison.Ordinal) && !Regex.IsMatch(u.Uses, @"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")).ToList();
        Assert.True(unpinned.Count == 0, "not pinned to a commit: " + string.Join(", ", unpinned.Select(u => $"{u.File}: {u.Uses}")));
    }
}
