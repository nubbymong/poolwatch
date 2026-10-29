using System.IO.Compression;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Incidents;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>AGENTS.md: embedded, written into the data folder only when needed and never through a link, and added to exports as it is.</summary>
public sealed class AgentsGuideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-agents-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();
    private readonly AppPaths _paths;
    private readonly Log _log;

    public AgentsGuideTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "test.log"));
    }

    public void Dispose()
    {
        bool gone = true;
        foreach (var l in _links) gone &= Links.DeleteLinkOnly(l);
        if (gone) try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string GuideFile => Path.Combine(_paths.DataRoot, AgentsGuide.FileName);

    [Fact]
    public void TheGuideIsEmbeddedWithItsEightSectionsInOrder()
    {
        string text = AgentsGuide.Text;
        Assert.StartsWith("# PoolWatch: a guide for AI assistants", text);
        string[] sections = ["## 1. What this folder is", "## 2. File map", "## 3. Reading `report.json`", "## 4. Known patterns", "## 5. Confirming a root cause",
                             "## 6. Redaction", "## 7. Writing a vendor bug report", "## 8. What not to suggest"];
        int at = 0;
        foreach (var s in sections)
        {
            int i = text.IndexOf(s, at, StringComparison.Ordinal);
            Assert.True(i >= 0, s + " is missing or out of order");
            at = i;
        }
        // Written for people and their assistants: no internal setting names.
        foreach (var setting in new[] { "AutoClearEnabled", "TokenGuardEnabled", "AutoClearEveryHours", "AutoClearPoolGb" })
            Assert.DoesNotContain(setting, text);
    }

    [Fact]
    public void TheGuideAndAboutSayThatGitHubSeesTheAddressTheUpdateCheckComesFrom()
    {
        // The one connection PoolWatch makes itself carries nothing about the PC, but GitHub still sees where it comes from.
        string guide = AgentsGuide.Text.ReplaceLineEndings(" ");
        Assert.Contains("api.github.com", guide);
        Assert.Contains("GitHub sees the IP address it comes from", guide);
        Assert.Contains("GitHub sees your IP address", Ui.AboutDialog.Network);
    }

    [Fact]
    public void ItIsWrittenOnceAndLeftAloneWhileUnchanged()
    {
        AgentsGuide.EnsureInDataRoot(_paths, _log);
        Assert.Equal(AgentsGuide.Text, File.ReadAllText(GuideFile));
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(GuideFile, old);
        AgentsGuide.EnsureInDataRoot(_paths, _log);
        Assert.Equal(old, File.GetLastWriteTimeUtc(GuideFile));
        Assert.False(File.Exists(GuideFile + ".tmp"));
    }

    [Theory]
    [InlineData("an older guide")]
    [InlineData(null)] // as long as the current guide, one character different
    public void AChangedFileIsReplaced(string? old)
    {
        string text = AgentsGuide.Text;
        File.WriteAllText(GuideFile, old ?? text[..^1] + (text[^1] == 'x' ? 'y' : 'x'));
        AgentsGuide.EnsureInDataRoot(_paths, _log);
        Assert.Equal(text, File.ReadAllText(GuideFile));
    }

    [Fact]
    public void ALinkInItsPlaceIsNeverWrittenThrough()
    {
        string target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        Links.Junction(GuideFile, target);
        _links.Add(GuideFile);
        AgentsGuide.EnsureInDataRoot(_paths, _log);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.True(File.GetAttributes(GuideFile).HasFlag(FileAttributes.ReparsePoint));
        // Refused by the write policy itself, not only by File.Move failing on a junction.
        string log = File.ReadAllText(Path.Combine(_paths.DataRoot, "test.log"));
        Assert.Contains("AGENTS.md not written", log);
        Assert.Contains("PoolWatch never writes or deletes through links", log);
    }

    private static Dictionary<string, string> Guide => new() { [AgentsGuide.FileName] = AgentsGuide.Text };

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void ARedactedExportHasTheGuideAtItsRootUnchanged()
    {
        // The user name is a word the guide uses: redacting the guide would change it.
        var redaction = new RedactionInfo("guide", "BIGBOX", [@"C:\Users\guide"]);
        string incident = Path.Combine(_root, "incidents", "20261028-091200-Reff-66666552");
        Directory.CreateDirectory(incident);
        File.WriteAllText(Path.Combine(incident, "report.md"), "guide ran C:\\Users\\guide\\x.exe on BIGBOX");
        File.WriteAllText(Path.Combine(incident, AgentsGuide.FileName), "a stale copy");

        string zipPath = Exporter.Export(incident, Path.Combine(_root, "exports"), redaction, p => p, Guide);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal([AgentsGuide.FileName, "report.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(AgentsGuide.Text, Entry(zip, AgentsGuide.FileName));
        Assert.Equal("<USER> ran <HOME>\\x.exe on <MACHINE>", Entry(zip, "report.md"));
    }

    [Fact]
    public void ARedactedVendorBundleHasTheGuideUnchangedToo()
    {
        var redaction = new RedactionInfo("guide", "BIGBOX", [@"C:\Users\guide"]);
        string zipPath = VendorBundle.Write(Path.Combine(_root, "exports"), DateTime.UtcNow, new Dictionary<string, string> { ["system.json"] = "{\"machine\": \"BIGBOX\"}" },
            redaction, p => p, Guide);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(AgentsGuide.Text, Entry(zip, AgentsGuide.FileName));
        Assert.Equal("{\"machine\": \"<MACHINE>\"}", Entry(zip, "system.json"));
        Assert.Contains("`AGENTS.md`", VendorBundle.Readme(true, DateTime.UtcNow, "1.0.0"));
    }
}
