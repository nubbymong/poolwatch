using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PoolWatch.AppTests;

/// <summary>
/// Every <c>dotnet restore</c> command a workflow runs is locked to packages.lock.json with --locked-mode, the form OpenSSF
/// Scorecard's Pinned-Dependencies check reads as pinned, and no workflow restores with nuget or msbuild /t:restore. These
/// tests check only that. Build and test commands take locked mode from Directory.Build.props (CI=true). The launcher's
/// NativeAOT publish runs unlocked on purpose, as ci.yml and docs/TECHNICAL.md say (its compiler packages come at the
/// SDK's own versions, which no lock file records), and so does the step that looks those packages up again with the
/// publish's properties. The Linux job's compile check of the app is one of the locked restores, which holds because the
/// app's project prunes System.CodeDom off Windows as the SDK does on Windows.
/// </summary>
public sealed class LockedRestoreTests
{
    private static string Repo(params string[] parts) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. parts])).Replace("\r\n", "\n");

    /// <summary>Each workflow line (comments aside), with its file's name.</summary>
    private static List<(string File, string Line)> WorkflowLines() =>
        Directory.GetFiles(Path.Combine(TrayIconsTests.RepoRoot(), ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetFileName(f), Line: l.Trim())))
            .Where(x => !x.Line.StartsWith('#'))
            .ToList();

    [Fact]
    public void EveryDotnetRestoreInTheWorkflowsIsLocked()
    {
        var lines = WorkflowLines();
        var restores = lines.Where(x => Regex.IsMatch(x.Line, @"(?<![\w-])dotnet(?:\.exe)?\s+restore\b", RegexOptions.IgnoreCase)).ToList();
        // The Linux job's compile check of the app is one of them.
        Assert.Contains(("ci.yml", "- run: dotnet restore src/PoolWatch/PoolWatch.csproj --locked-mode"), restores);
        foreach (var (file, line) in restores)
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(words.Contains("--locked-mode", StringComparer.OrdinalIgnoreCase), $"{file}: {line}");
            Assert.False(words.Any(w => w.Equals("--force-evaluate", StringComparison.OrdinalIgnoreCase) ||
                                        w.Contains("RestoreLockedMode=false", StringComparison.OrdinalIgnoreCase)), $"{file}: {line}");
        }
        // No restore of another form, which would need its own locked switch.
        Assert.DoesNotContain(lines, x => Regex.IsMatch(x.Line, @"(?<![\w-])nuget(?:\.exe)?\s+restore\b|/t:restore\b", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void OffWindowsTheAppPrunesSystemCodeDomAsTheSdkDoesOnWindows()
    {
        var project = XDocument.Parse(Repo("src", "PoolWatch", "PoolWatch.csproj")).Root!;
        var target = Assert.Single(project.Elements("Target"), t => (string?)t.Attribute("Name") == "PruneSystemCodeDomAsOnWindows");
        // After the SDK's own list is made, and only off Windows: on Windows the list already prunes it.
        Assert.Equal("AddPrunePackageReferences", (string?)target.Attribute("AfterTargets"));
        Assert.Equal("!$([MSBuild]::IsOSPlatform('Windows'))", (string?)target.Attribute("Condition"));
        var items = target.Elements("ItemGroup").Elements("PrunePackageReference").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("System.CodeDom", (string?)items[0].Attribute("Remove"));
        Assert.Equal("System.CodeDom", (string?)items[1].Attribute("Include"));
        // The version the SDK gives it on Windows: the major.minor of the version the Windows Desktop targeting pack lists
        // (10.0.14 in the 10.0.14 pack), which is the target framework's, and any patch: 10.0.32767 for net10.0-windows.
        Assert.Equal("$([MSBuild]::GetTargetFrameworkVersion('$(TargetFramework)', 2)).32767", (string?)items[1].Attribute("Version"));
        Assert.Equal("net10.0-windows", (string?)project.Elements("PropertyGroup").Elements("TargetFramework").Single());
    }
}
