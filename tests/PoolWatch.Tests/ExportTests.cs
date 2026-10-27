using System.IO.Compression;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Tests;

public class ExportTests
{
    private static readonly RedactionInfo Owner = new("alice", "BIGBOX", [@"C:\Users\alice"], "BIGBOX");

    [Fact]
    public void RedactsUserHomeAndMachine()
    {
        string text = @"user BIGBOX\alice ran C:\Users\alice\AppData\x.exe on bigbox; json ""C:\\Users\\alice\\x""; aliceblue stays";
        string r = Redactor.Redact(text, Owner);
        Assert.DoesNotContain("alice\\", r);
        Assert.DoesNotContain("BIGBOX", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"<HOME>\AppData\x.exe", r);
        Assert.Contains(@"<DOMAIN>\<USER>", r);
        Assert.Contains("aliceblue stays", r); // whole words only
    }

    [Fact]
    public void RedactsJsonEscapedNonAsciiAndSid()
    {
        var r = new RedactionInfo("Jörg", "BIGBOX", [@"C:\Users\Jörg"], "BIGBOX", "S-1-5-21-1-2-3-1001");
        string json = System.Text.Json.JsonSerializer.Serialize(new { path = @"C:\Users\Jörg\x.exe", user = @"BIGBOX\Jörg", sid = "S-1-5-21-1-2-3-1001" });
        string red = Redactor.Redact(json, r);
        Assert.DoesNotContain("rg", red.Replace("<", "").Replace(">", "").Replace("\"", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21", red);
        var doc = System.Text.Json.JsonDocument.Parse(red); // still valid JSON
        Assert.Equal(@"<HOME>\x.exe", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(@"<DOMAIN>\<USER>", doc.RootElement.GetProperty("user").GetString());
    }

    [Fact]
    public void HomePathNeedsABoundaryAndCommonNamesOnlyInPaths()
    {
        Assert.Equal(@"C:\Users\alice2\a and <HOME>\b", Redactor.Redact(@"C:\Users\alice2\a and C:\Users\alice\b", Owner));
        var plain = new RedactionInfo("User", "PC1", [@"C:\Users\User"], "PC1");
        Assert.Equal(@"<DOMAIN>\<USER> ran <HOME>\x; the user said", Redactor.Redact(@"PC1\User ran C:\Users\User\x; the user said", plain));
    }

    [Fact]
    public void ExportZipsRedactedTextAndOmitsTrace()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-export-" + Guid.NewGuid().ToString("N"));
        string inc = Path.Combine(root, "incidents", "20261020-060000-File-656c6946");
        Directory.CreateDirectory(inc);
        try
        {
            File.WriteAllText(Path.Combine(inc, "report.md"), @"C:\Users\alice\find.exe on BIGBOX");
            File.WriteAllBytes(Path.Combine(inc, "capture.etl"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(inc, "timeline.png"), [0x89, 0x50]);
            Directory.CreateDirectory(Path.Combine(inc, "wpr-temp"));
            File.WriteAllBytes(Path.Combine(inc, "wpr-temp", "buffer.etl"), [1]);
            string zip = Exporter.Export(inc, Path.Combine(root, "exports"), Owner, p => p);
            using var z = ZipFile.OpenRead(zip);
            using (var r = new StreamReader(z.GetEntry("report.md")!.Open())) Assert.Equal(@"<HOME>\find.exe on <MACHINE>", r.ReadToEnd());
            Assert.Null(z.GetEntry("capture.etl"));
            Assert.NotNull(z.GetEntry("capture.etl.omitted.txt"));
            Assert.NotNull(z.GetEntry("timeline.png"));
            Assert.DoesNotContain(z.Entries, e => e.FullName.StartsWith("wpr-temp"));
            Assert.EndsWith("-redacted.zip", zip);
        }
        finally { Directory.Delete(root, true); }
    }
}
