using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// THIRD-PARTY-NOTICES.md's "What ships" table names every NuGet package the shipped programs resolve (the lock files of
/// the app and of the launcher), each at the version its lock file resolves it to, and no package they do not resolve.
/// An update pull request (Dependabot's) changes the project and its lock files and never the notices, so without this
/// a release would ship a new version under the old one's notice.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    /// <summary>
    /// The packages of the lock files that are not shipped, so not in the table: each resolves to the shared framework,
    /// as the notices say under "What was checked".
    /// </summary>
    private static readonly string[] NotShipped = ["System.Reflection.TypeExtensions"];

    /// <summary>A NuGet package's name, as the table's first column gives one (".NET runtime" or a link is not one).</summary>
    private static readonly Regex PackageName = new(@"^[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+$");

    private static string Repo(params string[] parts) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. parts])).Replace("\r\n", "\n");

    /// <summary>
    /// The packages a lock file resolves for its project's target framework (the one section whose name has no runtime
    /// after a slash), each with its resolved version; project references left out.
    /// </summary>
    internal static Dictionary<string, string> LockedPackages(string lockJson)
    {
        using var doc = JsonDocument.Parse(lockJson);
        var framework = Assert.Single(doc.RootElement.GetProperty("dependencies").EnumerateObject(), s => !s.Name.Contains('/')).Value;
        return framework.EnumerateObject()
            .Where(p => p.Value.GetProperty("type").GetString() != "Project")
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("resolved").GetString()!);
    }

    /// <summary>The rows of the "What ships" table, each as its cells; the header and the rule below it left out.</summary>
    internal static List<string[]> ShipsRows(string notices)
    {
        int start = notices.IndexOf("\n## What ships\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "THIRD-PARTY-NOTICES.md has no \"What ships\" section");
        int end = notices.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var rows = notices[start..(end < 0 ? notices.Length : end)].Split('\n')
            .Where(l => l.StartsWith('|'))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        Assert.True(rows.Count > 2 && rows[0][0] == "Component" && rows[0][1] == "Version" && rows[1][0].StartsWith("---", StringComparison.Ordinal),
            "the \"What ships\" table does not start with its Component and Version header");
        return rows.Skip(2).ToList();
    }

    /// <summary>
    /// The version each package named in the table's first column is given: the names come before the cell's
    /// parenthesis, separated by commas, and the Version cell gives one version for all of them or one for each, in the
    /// same order ("8.0.3 and 8.0.2").
    /// </summary>
    internal static Dictionary<string, string> StatedVersions(IEnumerable<string[]> rows)
    {
        var stated = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            string component = row[0];
            int paren = component.IndexOf(" (", StringComparison.Ordinal);
            var names = (paren < 0 ? component : component[..paren]).Split(',').Select(n => n.Trim()).Where(n => PackageName.IsMatch(n)).ToList();
            if (names.Count == 0) continue;
            var versions = Regex.Matches(row[1], @"(?<![\w.])[0-9]+(?:\.[0-9]+){2,3}(?:-[0-9A-Za-z.-]*[0-9A-Za-z])?(?![\w.])").Select(m => m.Value).ToList();
            Assert.True(versions.Count == 1 || versions.Count == names.Count,
                $"the row for {string.Join(", ", names)} gives {versions.Count} versions (\"{row[1]}\"): one for all of them, or one for each");
            for (int i = 0; i < names.Count; i++)
                Assert.True(stated.TryAdd(names[i], versions[versions.Count == 1 ? 0 : i]), $"{names[i]} has more than one row");
        }
        return stated;
    }

    [Fact]
    public void WhatShipsNamesEveryPackageOfTheShippedProgramsAtItsLockedVersionAndNoOther()
    {
        var locked = new Dictionary<string, string>();
        foreach (string project in new[] { "PoolWatch", "PoolWatch.Launcher" })
            foreach (var (name, version) in LockedPackages(Repo("src", project, "packages.lock.json")))
                Assert.True(locked.TryAdd(name, version) || locked[name] == version, $"{name} is {locked[name]} and {version} in the lock files");
        string notices = Repo("THIRD-PARTY-NOTICES.md");
        string flat = Regex.Replace(notices, @"\s+", " ");
        var rows = ShipsRows(notices);
        var stated = StatedVersions(rows);

        foreach (string name in NotShipped)
        {
            Assert.True(locked.ContainsKey(name), $"{name} is no longer in the lock files: take it out of NotShipped, and out of the notices' sentence on what is not shipped");
            Assert.Contains($"`{name}`", notices);
            Assert.False(stated.ContainsKey(name), $"{name} is not shipped, but the \"What ships\" table names it");
        }
        var shipped = locked.Where(p => !NotShipped.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var missing = shipped.Keys.Where(n => !stated.ContainsKey(n)).Order().ToList();
        Assert.True(missing.Count == 0, "THIRD-PARTY-NOTICES.md's \"What ships\" table does not name " + string.Join(", ", missing.Select(n => $"{n} {shipped[n]}")));
        var extra = stated.Keys.Where(n => !shipped.ContainsKey(n)).Order().ToList();
        Assert.True(extra.Count == 0, "THIRD-PARTY-NOTICES.md's \"What ships\" table names packages the lock files do not have: " + string.Join(", ", extra));
        foreach (var (name, version) in shipped)
            Assert.True(stated[name] == version, $"THIRD-PARTY-NOTICES.md gives {name} {stated[name]}, the lock file {version}");

        // The native DLLs' row names the TraceEvent they come from, and the note above the licence texts the
        // System.Management it describes.
        string traceEvent = shipped["Microsoft.Diagnostics.Tracing.TraceEvent"];
        var native = Assert.Single(rows, r => r[0] == "TraceEvent's native DLLs");
        Assert.Equal($"from TraceEvent {traceEvent}", native[1]);
        Assert.Contains($"System.Management {shipped["System.Management"]} carries only its", flat);
    }

    [Fact]
    public void TheTableIsReadAsItIsWritten()
    {
        // Names before the parenthesis, a version for each in order or one for all, and the rows that name no package
        // (the runtime, the launcher's parts, the build tools, Inno Setup) passed over.
        string[][] rows =
        [
            ["A.B ([upstream](https://example.com/a.b))", "1.2.3", "x", "MIT"],
            ["A.C, A.D ([upstream](https://example.com))", "8.0.3 and 8.0.2", "x", "MIT"],
            ["A.E, A.F", "6.0.0", "x", "MIT"],
            ["A.P", "2.0.0-preview.3", "x", "MIT"],
            [".NET runtime and Windows Desktop runtime (A.App, A.Desktop.App)", "the .NET 10 patch the release was built with", "x", "MIT"],
            ["[Inno Setup](https://jrsoftware.org/isinfo.php)", "6.7.3", "x", "x"],
            ["TraceEvent's native DLLs", "from TraceEvent 1.2.3", "x", "x"],
        ];
        var stated = StatedVersions(rows);
        Assert.Equal(new Dictionary<string, string> { ["A.B"] = "1.2.3", ["A.C"] = "8.0.3", ["A.D"] = "8.0.2", ["A.E"] = "6.0.0", ["A.F"] = "6.0.0", ["A.P"] = "2.0.0-preview.3" }, stated);
        // Two versions for three names, and one name in two rows, are refused.
        Assert.ThrowsAny<Exception>(() => StatedVersions([["A.C, A.D, A.G", "8.0.3 and 8.0.2", "x", "x"]]));
        Assert.ThrowsAny<Exception>(() => StatedVersions([["A.B", "1.2.3", "x", "x"], ["A.B, A.C", "1.2.4", "x", "x"]]));
        // A lock file: the target framework's section, not the runtime's, and no project.
        var locked = LockedPackages("""
            { "version": 1, "dependencies": {
              "net10.0-windows7.0": {
                "A.B": { "type": "Direct", "requested": "[1.2.3, )", "resolved": "1.2.3" },
                "A.C": { "type": "Transitive", "resolved": "8.0.3" },
                "a.project": { "type": "Project" } },
              "net10.0-windows7.0/win-x64": { "A.R": { "type": "Direct", "resolved": "9.9.9" } } } }
            """);
        Assert.Equal(new Dictionary<string, string> { ["A.B"] = "1.2.3", ["A.C"] = "8.0.3" }, locked);
    }
}
