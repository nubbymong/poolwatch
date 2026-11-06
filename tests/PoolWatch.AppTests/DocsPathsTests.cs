using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The documents name each registry key whole: a key's separators are never lost ("HKLMSOFTWAREPoolWatch" for
/// <c>HKLM\SOFTWARE\PoolWatch</c>), as a merge that drops backslashes would leave it, in any Markdown file of the repo.
/// </summary>
public sealed class DocsPathsTests
{
    /// <summary>A root key or SOFTWARE run into the next part of the path.</summary>
    internal static readonly Regex GluedKey = new(@"\b(?:HKLM|HKCU|HKCR|HKU|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER)(?=[A-Za-z])|\bSOFTWARE(?=[A-Z][a-z])");

    private static IEnumerable<string> MarkdownFiles()
    {
        string root = TrayIconsTests.RepoRoot();
        char sep = Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}.git{sep}")) continue;
            yield return file;
        }
    }

    [Fact]
    public void EveryRegistryKeyTheDocsNameKeepsItsSeparators()
    {
        var files = MarkdownFiles().ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == "SECURITY.md");
        var glued = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => GluedKey.IsMatch(x.l)).Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {GluedKey.Match(x.l).Value}...").ToList();
        Assert.True(glued.Count == 0, string.Join(Environment.NewLine, glued));
        // SECURITY.md's uninstaller row names the key that records a data folder of PoolWatch's own whole.
        string security = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "SECURITY.md"));
        Assert.Contains(@"the record of that folder in `HKLM\SOFTWARE\PoolWatch` stays too", security, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanFindsAKeyWithItsSeparatorsLost()
    {
        Assert.Matches(GluedKey, "the record of that folder in `HKLMSOFTWAREPoolWatch` stays too");
        Assert.Matches(GluedKey, "HKEY_LOCAL_MACHINESOFTWARE");
        Assert.Matches(GluedKey, @"HKLM\SOFTWAREPoolWatch");
        Assert.DoesNotMatch(GluedKey, @"`HKLM\SOFTWARE\PoolWatch` (value `DataFolder`)");
        Assert.DoesNotMatch(GluedKey, "the StartupApproved switch in HKCU / HKLM that Task Manager uses");
        Assert.DoesNotMatch(GluedKey, @"HKEY_CURRENT_USER\Software\Microsoft");
    }
}
