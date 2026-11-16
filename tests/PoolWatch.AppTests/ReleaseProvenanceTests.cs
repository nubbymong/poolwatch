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
    /// The lines of the block under a key at <paramref name="indent"/> (a key of the job itself by default; "" for a key
    /// of the workflow's), as "key: value" in order, comments left out; none when there is no such block. As YAML reads
    /// it, the block may follow a comment on the key's line and goes on past blank lines and comment lines, up to the next
    /// line that is not one of those and is indented no deeper than the key.
    /// </summary>
    private static List<string> Under(string text, string key, string indent = "    ")
    {
        var block = Regex.Match(text, $@"(?m)^{indent}{Regex.Escape(key)}:[ \t]*(?:#.*)?\n(?<lines>(?:(?:{indent}  .*|[ \t]*(?:#.*)?)\n)*)");
        if (!block.Success) return [];
        return block.Groups["lines"].Value.Split('\n')
            .Select(l => Regex.Replace(l, @"(^|\s+)#.*$", "").Trim())
            .Where(l => l.Length > 0)
            .Select(l => Regex.Replace(l, @":\s+", ": "))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What follows the key on each line of a key at <paramref name="indent"/>, comments left out: "" for the block form,
    /// the rights themselves for a one-line form ("write-all", "{ id-token: write }").
    /// </summary>
    private static List<string> KeyLines(string text, string key, string indent) =>
        Regex.Matches(text, $@"(?m)^{indent}{Regex.Escape(key)}:(?<v>.*)$")
            .Select(m => Regex.Replace(m.Groups["v"].Value, @"(^|\s+)#.*$", "").Trim())
            .ToList();

    /// <summary>How many times <paramref name="part"/> is in <paramref name="text"/>.</summary>
    private static int Occurrences(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    /// <summary>
    /// What is wrong with the rights the workflow <paramref name="ci"/> asks for, each in plain words (none: nothing). The
    /// workflow's own token is contents: read and nothing more. The provenance job may hold id-token, attestations and
    /// contents write, the publish job contents write, and every other job contents read at most. The workflow and each
    /// job have at most one permissions key, in the block form ("permissions:" and one "scope: access" line under it
    /// each), the form this check reads: a one-line form ("permissions: write-all", "permissions: { id-token: write }")
    /// fails, so that no job can hold a right the check does not see.
    /// </summary>
    private static List<string> RightsProblems(string ci)
    {
        var problems = new List<string>();
        var own = KeyLines(ci, "permissions", "");
        if (own.Count != 1) problems.Add($"the workflow has {own.Count} permissions keys of its own, not one");
        problems.AddRange(own.Where(v => v.Length > 0).Select(v => $"the workflow's own permissions are on one line ({v}), not a block"));
        var ownRights = Under(ci, "permissions", "");
        if (ownRights.Count != 1 || ownRights[0] != "contents: read")
            problems.Add($"the workflow's own token is not contents: read alone ({string.Join(", ", ownRights)})");
        foreach (var (name, job) in Jobs(ci))
        {
            string[] allowed = name switch
            {
                "provenance" => ["attestations: write", "contents: write", "id-token: write"],
                "publish" => ["contents: write"],
                _ => ["contents: read"],
            };
            var keys = KeyLines(job, "permissions", "    ");
            if (keys.Count > 1) problems.Add($"the job {name} has {keys.Count} permissions keys");
            problems.AddRange(keys.Where(v => v.Length > 0).Select(v => $"the job {name} asks for permissions: {v} on one line, not as a block"));
            var more = Under(job, "permissions").Where(r => !allowed.Contains(r)).ToList();
            if (more.Count > 0) problems.Add($"the job {name} asks for {string.Join(", ", more)}");
        }
        return problems;
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
    /// and the provenance job's two name the same files in the same order. The job checks each file on the release against
    /// the attestation (signed by ci.yml on a GitHub-hosted runner, for the tag and its commit), and only once all pass
    /// attaches it to the release, in its one upload, under the name Scorecard's Signed-Releases check looks for
    /// (.intoto.jsonl). Last, the release's copy must be the attestation, byte for byte. So the release never carries
    /// provenance that its files do not match.
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

        // Its last step, failing on the first command that fails, checks each file on the release against the bundle,
        // then attaches the bundle as an .intoto.jsonl file, then checks that the release's copy is the bundle: these
        // lines, each after the one before it.
        string[] order =
        [
            "\n      - name: Check the release's files against the provenance, then attach it to the release\n",
            "\n          set -euo pipefail\n",
            "\n          name=\"PoolWatch-$GITHUB_REF_NAME.intoto.jsonl\"\n",
            "\n          cp \"$BUNDLE\" \"$RUNNER_TEMP/$name\"\n",
            "\n          for f in ",
            "\n            gh release download \"$GITHUB_REF_NAME\" --repo \"$GITHUB_REPOSITORY\" --pattern \"$f\"\n",
            "\n            gh attestation verify \"$f\" --bundle \"$RUNNER_TEMP/$name\" --repo \"$GITHUB_REPOSITORY\" \\\n"
                + "              --signer-workflow \"$GITHUB_REPOSITORY/.github/workflows/ci.yml\" --source-ref \"$GITHUB_REF\" --source-digest \"$GITHUB_SHA\" --deny-self-hosted-runners\n",
            "\n          done\n",
            "\n          gh release upload \"$GITHUB_REF_NAME\" \"$RUNNER_TEMP/$name\" --repo \"$GITHUB_REPOSITORY\" --clobber\n",
            "\n          gh release download \"$GITHUB_REF_NAME\" --repo \"$GITHUB_REPOSITORY\" --pattern \"$name\" --dir \"$RUNNER_TEMP/attached\"\n",
            "\n          cmp \"$BUNDLE\" \"$RUNNER_TEMP/attached/$name\"\n",
        ];
        int at = 0;
        foreach (string line in order)
        {
            int next = provenance.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(next >= 0, $"the provenance job has no line, after the one before it in this test's list: {line.Trim()}");
            at = next + 1;
        }
        // The upload above is the job's only one: nothing goes on the release before every file passes.
        Assert.Single(Regex.Matches(provenance, "gh release upload"));
    }

    /// <summary>
    /// The workflow's own token is read-only, and its triggers are the four it had (no pull_request_target, so a fork's
    /// pull request never runs with a write token or a secret). Of the jobs, only the provenance job may get the run's
    /// identity token (to sign) and keep an attestation, and only it and the publish job may write the repository's
    /// contents; the provenance job asks for nothing more. Every permissions key is a block, which the check reads.
    /// </summary>
    [Fact]
    public void OnlyTheProvenanceJobHoldsItsRightsAndTheWorkflowsOwnStayReadOnly()
    {
        string ci = Ci();
        Assert.Contains("\non:\n  push:\n    branches: [main]\n    tags: ['v*']\n  pull_request:\n  workflow_dispatch:\n\npermissions:\n  contents: read\n\n", ci);
        Assert.Equal(["attestations: write", "contents: write", "id-token: write"], Under(Jobs(ci)["provenance"], "permissions"));
        var problems = RightsProblems(ci);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// The rights check fails rights written in a form it does not read as a block: on one line, for a job or for the
    /// workflow, or as a block with a comment on its key's line or a blank line in it. Each case is ci.yml as it is with
    /// one change, made here in memory and not in the file: to a job that has no permissions key of its own, or to the
    /// workflow's own. The check before this one read a job's rights only from a block right under its key, so for a job's
    /// one-line form it read no rights and passed the job; each such case first shows that.
    /// </summary>
    [Theory]
    [InlineData("build", "    permissions: write-all\n", true)]
    [InlineData("build", "    permissions: read-all # every scope, read only\n", true)]
    [InlineData("e2e", "    permissions: { id-token: write, attestations: write }\n", true)]
    [InlineData("build", "    permissions: # the job's rights\n      id-token: write\n", false)]
    [InlineData("build", "    permissions:\n      contents: read\n\n      id-token: write\n", false)]
    [InlineData("", "permissions: write-all\n", true)]
    public void TheRightsCheckFailsRightsItCannotReadAsABlock(string job, string permissions, bool oneLine)
    {
        string ci = Ci(), changed;
        var before = RightsProblems(ci);
        Assert.True(before.Count == 0, "ci.yml as it is fails the rights check: " + string.Join("; ", before));
        if (job.Length == 0)
        {
            const string own = "\npermissions:\n  contents: read\n";
            Assert.True(Occurrences(ci, own) == 1, "ci.yml's own permissions are not the one block this test changes");
            changed = ci.Replace(own, "\n" + permissions, StringComparison.Ordinal);
        }
        else
        {
            string header = $"\n  {job}:\n";
            Assert.True(Occurrences(ci, header) == 1, $"ci.yml has not one job {job}");
            Assert.Null(Key(Jobs(ci)[job], "permissions"));
            changed = ci.Replace(header, header + permissions, StringComparison.Ordinal);
            // What the check before this one read: no block under the key, so no rights, and nothing to fail.
            if (oneLine) Assert.Empty(Under(Jobs(changed)[job], "permissions"));
        }
        Assert.NotEqual(ci, changed);
        string who = job.Length == 0 ? "the workflow" : $"the job {job} ";
        Assert.Contains(RightsProblems(changed), p => p.StartsWith(who, StringComparison.Ordinal));
    }

    /// <summary>
    /// docs/TECHNICAL.md gives users the provenance job's own check, as far as a user can give it: this repository, ci.yml
    /// as the signer, the release's tag as the source ref, and a GitHub-hosted runner. The job checks the release's files
    /// that way before it attaches the provenance, so a file that passes there passes for the user too.
    /// </summary>
    [Fact]
    public void TheDocumentedCheckAsksWhatTheProvenanceJobsOwnCheckAsks()
    {
        string doc = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "docs", "TECHNICAL.md")).Replace("\r\n", "\n");
        var commands = Regex.Matches(doc, @"(?m)^gh attestation verify .*$").Select(m => m.Value).ToList();
        string command = Assert.Single(commands) + " ";
        foreach (string flag in new[] { "--repo nubbymong/poolwatch ", "--signer-workflow nubbymong/poolwatch/.github/workflows/ci.yml ", "--source-ref refs/tags/vX.Y.Z ", "--deny-self-hosted-runners " })
            Assert.Contains(flag, command);
        Assert.Contains(" --source-ref \"$GITHUB_REF\" ", Jobs(Ci())["provenance"]);
        Assert.Contains(" --deny-self-hosted-runners\n", Jobs(Ci())["provenance"]);
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
