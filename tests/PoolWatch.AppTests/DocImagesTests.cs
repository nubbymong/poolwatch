using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// Every picture the front page and the docs show is in the repository: each Markdown image, and each HTML img or
/// source, in README.md and docs\*.md that names a file (not a web address) names one that is there, so none shows as a
/// broken image. A screenshot of a new window is linked once the CI UI run's ui-screenshots artifact has given it and it
/// was copied into docs\screenshots.
/// </summary>
public sealed class DocImagesTests
{
    /// <summary>The files a text's images name: Markdown's <c>![...](path)</c>, HTML's <c>src</c> and each of a <c>srcset</c>'s entries.</summary>
    internal static IEnumerable<string> ImagesIn(string text)
    {
        foreach (Match m in Regex.Matches(text, @"!\[[^\]]*\]\((?<p>[^)\s]+)[^)]*\)|\bsrc=""(?<p>[^""]+)""|\bsrcset=""(?<set>[^""]+)"""))
        {
            if (m.Groups["p"].Success) yield return m.Groups["p"].Value;
            else
                foreach (string entry in m.Groups["set"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return entry.Split(' ')[0];
        }
    }

    [Fact]
    public void EveryImageTheDocsShowIsThere()
    {
        string root = TrayIconsTests.RepoRoot();
        var docs = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Prepend(Path.Combine(root, "README.md")).ToList();
        var missing = new List<string>();
        int seen = 0;
        foreach (string doc in docs)
            foreach (string image in ImagesIn(File.ReadAllText(doc)))
            {
                if (Regex.IsMatch(image, "^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)) continue; // a web address
                seen++;
                string relative = Uri.UnescapeDataString(image.Split('#', '?')[0]).Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(doc)!, relative))))
                    missing.Add($"{Path.GetRelativePath(root, doc)}: {image}");
            }
        Assert.True(seen >= 20, $"only {seen} images found in the docs: the reading missed them");
        Assert.True(missing.Count == 0, "images the docs show that are not in the repository: " + string.Join("; ", missing));
    }

    /// <summary>The reading finds each kind of image, and leaves web addresses to the check above to pass over.</summary>
    [Fact]
    public void TheReadingFindsEveryKindOfImage()
    {
        Assert.Equal(["screenshots/a.png", "docs/b.png", "docs/c-dark.png", "docs/c-light.png", "https://example.com/d.svg"],
            ImagesIn("![A](screenshots/a.png) <img alt=\"B\" src=\"docs/b.png\"> <source srcset=\"docs/c-dark.png 1x, docs/c-light.png 2x\"> " +
                     "<img src=\"https://example.com/d.svg\">"));
        Assert.Equal(["screenshots/e.png"], ImagesIn("![E](screenshots/e.png \"a title\")"));
    }
}
