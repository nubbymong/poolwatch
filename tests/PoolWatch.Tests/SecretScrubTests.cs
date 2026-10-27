using System.IO.Compression;
using System.Text.Json;
using PoolWatch.Core;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// M1 (security review): evidence packs are attached to public bug reports. The incident folder is written as recorded
/// (with a notice saying so), and every redacted export, vendor bundle and redacted copy also scrubs values that look like
/// secrets out of command lines and transcript text, which the identity redaction never touched.
/// </summary>
public class SecretScrubTests
{
    private static readonly RedactionInfo Alice = new("alice", "ALICE-PC", [@"C:\Users\alice"], "ALICE-PC");

    [Theory]
    [InlineData("x.exe --password hunter2 -v", "x.exe --password <SECRET> -v")]
    [InlineData("x.exe --password=\"hunter two\" -v", "x.exe --password=<SECRET> -v")]
    [InlineData("x.exe --db-password 'hunter two'", "x.exe --db-password <SECRET>")]
    [InlineData("tool.exe /token:abc123 /v", "tool.exe /token:<SECRET> /v")]
    [InlineData("set API_KEY=abc123 && run", "set API_KEY=<SECRET> && run")]
    [InlineData("MY_SERVICE_TOKEN=abc123 node app.js", "MY_SERVICE_TOKEN=<SECRET> node app.js")]
    [InlineData("curl -H \"Authorization: Bearer abcdefghijkl\" https://example.com", "curl -H \"Authorization: Bearer <SECRET>\" https://example.com")]
    [InlineData("curl -H \"X-Api-Key: abcdef123456\"", "curl -H \"X-Api-Key: <SECRET>\"")]
    [InlineData("git clone https://alice:s3cr3t@example.com/r.git", "git clone https://<SECRET>@example.com/r.git")]
    [InlineData("Server=db;User Id=sa;Password=p4ss;Encrypt=true", "Server=db;User Id=sa;Password=<SECRET>;Encrypt=true")]
    [InlineData("https://acct.blob.core.windows.net/c?sv=2024-01-01&sig=abc%2Bdef", "https://acct.blob.core.windows.net/c?sv=2024-01-01&sig=<SECRET>")]
    public void SecretsInCommandLinesAreScrubbedAndTheOptionNameStays(string text, string expected) =>
        Assert.Equal(expected, SecretScrubber.Scrub(text));

    [Fact]
    public void WellKnownTokenShapesAreScrubbed()
    {
        // N4: the token shapes are built here at run time (not written as literals), so GitHub secret-scanning does not
        // flag this test file on the public repo. They are synthetic, not real keys.
        foreach (var (token, before, after) in new[]
        {
            ("sk-ant-api03-" + new string('a', 24), "claude --key ", ""),
            ("gh" + "p_" + new string('b', 36), "gh auth login ", ""),
            ("AKIA" + new string('C', 16), "aws ", " s3 ls"),
            ("eyJ" + new string('a', 10) + ".eyJ" + new string('b', 10) + "." + new string('c', 12), "x ", " y"),
        })
        {
            Assert.Equal(before + "<SECRET>" + after, SecretScrubber.Scrub(before + token + after));
            Assert.DoesNotContain(token, SecretScrubber.Scrub(before + token + after));
        }
    }

    [Theory]
    [InlineData("Token leak guard: workaround ON (foreground lock timeout 0: any app may take the focus)")]
    [InlineData("Token leak guard: last test: clean-once on 26100.1000; Toke outstanding 1,234 (5 MB).")]
    [InlineData("the token-leak guard's test started 600 children")]
    [InlineData(@"git -C D:\repo status --porcelain")]
    [InlineData("find / -maxdepth 6 -type d -name release-notes-*")]
    [InlineData("npm run build --workspace web")]
    [InlineData("signer: Microsoft Windows; signature: valid")]
    [InlineData("Toke vs process starts: collecting the first buckets")]
    public void PoolWatchsOwnTextIsLeftAlone(string text) => Assert.Equal(text, SecretScrubber.Scrub(text));

    [Theory]
    [InlineData(false)] // the default encoder writes a quote as " (the vendor bundle's JSON)
    [InlineData(true)]  // the relaxed encoder writes it as \" (report.json, findings)
    public void JsonStaysValidAndLosesTheSecret(bool relaxed)
    {
        var options = relaxed ? new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping } : null;
        string json = JsonSerializer.Serialize(new
        {
            commandLine = "x.exe --token \"abc def\" --user bob",
            trailing = "y.exe --password ",          // an option with nothing after it, at the end of a string
            next = "z.exe --api-key=k1+23 run",      // '+' is + in the default encoder's output
        }, options);
        string scrubbed = SecretScrubber.Scrub(json, structured: true);
        using var doc = JsonDocument.Parse(scrubbed); // still valid JSON
        Assert.Equal("x.exe --token <SECRET> --user bob", doc.RootElement.GetProperty("commandLine").GetString());
        Assert.Equal("y.exe --password ", doc.RootElement.GetProperty("trailing").GetString());
        Assert.Equal("z.exe --api-key=<SECRET> run", doc.RootElement.GetProperty("next").GetString());
        Assert.DoesNotContain("abc def", scrubbed);
        Assert.DoesNotContain("23", scrubbed);
    }

    [Fact]
    public void CsvStaysValidAndLosesTheSecret()
    {
        string line = Csv.Line("x.exe --password \"hunter2\" -v", 5, "w.exe --token ", "after");
        string scrubbed = SecretScrubber.Scrub(line, structured: true);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.Equal(["x.exe --password <SECRET> -v", "5", "w.exe --token ", "after"], Csv.Split(scrubbed));
    }

    [Fact]
    public void AReportMdCodeSpanKeepsItsClosingBacktick()
    {
        // S1: report.md writes a command line inside a `code span` (MdCode turns any backtick in it into '). The scrub must
        // stop before the closing backtick, or GitHub renders an unbalanced span and drops the <SECRET> as an unknown tag.
        Assert.Equal("- Command line: `x.exe --password <SECRET>`", SecretScrubber.Scrub("- Command line: `x.exe --password hunter2`"));
        Assert.Equal("| `svc.exe --token <SECRET>` |", SecretScrubber.Scrub("| `svc.exe --token abc123` |"));
        // Start Y as `x --password p`. (the draft-bug-report shape): the span and the trailing full stop both survive.
        Assert.Equal("Start Y as `x --password <SECRET>`.", SecretScrubber.Scrub("Start Y as `x --password hunter2`."));
    }

    [Theory]
    [InlineData("x.exe --password Tr0ub&3x", "x.exe --password <SECRET>")]        // & kept in the value
    [InlineData("x.exe --token a|b;c,d", "x.exe --token <SECRET>")]               // | ; , all kept
    [InlineData("x.exe --token a<b>c)d]e}f", "x.exe --token <SECRET>")]           // < > ) ] }
    [InlineData(@"x.exe --password a\b\c", "x.exe --password <SECRET>")]          // a backslash in an unquoted value
    [InlineData("x.exe --token abc def", "x.exe --token <SECRET> def")]           // still stops at whitespace
    public void AnOptionValueIsTakenWholeUpToWhitespaceOrAQuote(string text, string expected) =>
        Assert.Equal(expected, SecretScrubber.Scrub(text));

    [Fact]
    public void OptionTakesSeparatorsButKeyValueStopsAtThem()
    {
        // The whole-value rule is for command-line options (one token, so ; and & are part of it)...
        Assert.Equal("app --password <SECRET>", SecretScrubber.Scrub("app --password a;b&c"));
        // ...while a key=value inside a connection string keeps ; as a separator (the rest of the string is not a secret).
        Assert.Equal("password=<SECRET>;keep=1", SecretScrubber.Scrub("password=a;keep=1"));
    }

    [Fact]
    public void AJsonValueEndingInABackslashStaysValid()
    {
        // N1: a relaxed-encoder JSON string whose value ends in a backslash. The \"...\" form must NOT eat the string's own
        // closing quote (the old pattern did, producing invalid JSON). After the fix the file still parses; this odd value
        // (an unterminated quote then a backslash) is left as recorded rather than mangled — best-effort, never corrupting.
        string json = JsonSerializer.Serialize(new { cmd = "x --token \"ab\\" }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string scrubbed = SecretScrubber.Scrub(json, structured: true);
        using var doc = JsonDocument.Parse(scrubbed); // still valid JSON (the N1 fix)
        Assert.Equal("x --token \"ab\\", doc.RootElement.GetProperty("cmd").GetString());
    }

    [Fact]
    public void RedactionAndCopiesScrubSecretsToo()
    {
        string text = @"C:\Users\alice\tool.exe --token abc123 on ALICE-PC";
        Assert.Equal(@"<HOME>\tool.exe --token <SECRET> on <MACHINE>", Redactor.Redact(text, Alice));
        Assert.Equal(@"<HOME>\tool.exe --token <SECRET> on <MACHINE>", CopyText.Apply(text, Alice));
        Assert.Equal(text, CopyText.Apply(text, null)); // redaction off: as recorded
    }

    [Fact]
    public void TheExportSwapsTheNoticeAndScrubsEveryTextFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-m1-" + Guid.NewGuid().ToString("N"));
        string inc = Path.Combine(root, "incidents", "20261020-060000-File-656c6946");
        Directory.CreateDirectory(inc);
        try
        {
            File.WriteAllText(Path.Combine(inc, "report.md"), $"# File grew\n\n> {Exporter.UnredactedNotice}\n\n- command line: `x.exe --password hunter2`\n");
            File.WriteAllText(Path.Combine(inc, "report.json"), JsonSerializer.Serialize(new { notice = Exporter.UnredactedNotice, commandLine = "x.exe --token \"abc def\"" }));
            File.WriteAllText(Path.Combine(inc, "processes.csv"), "pid,command_line\n" + Csv.Line(42, "x.exe --api-key=k123") + "\n");

            string zip = Exporter.Export(inc, Path.Combine(root, "exports"), Alice, p => p);
            using var z = ZipFile.OpenRead(zip);
            string Read(string name) => new StreamReader(z.GetEntry(name)!.Open()).ReadToEnd();
            string md = Read("report.md"), json = Read("report.json"), csv = Read("processes.csv");
            Assert.Contains(Exporter.RedactedNotice, md);
            Assert.DoesNotContain("NOT REDACTED", md);
            Assert.DoesNotContain("hunter2", md);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(Exporter.RedactedNotice, doc.RootElement.GetProperty("notice").GetString());
            Assert.Equal("x.exe --token <SECRET>", doc.RootElement.GetProperty("commandLine").GetString());
            Assert.DoesNotContain("k123", csv);

            // Not redacted (the user chose No): exactly as recorded, notice included.
            string raw = Exporter.Export(inc, Path.Combine(root, "exports-raw"), null, p => p);
            using var r = ZipFile.OpenRead(raw);
            Assert.Contains(Exporter.UnredactedNotice, new StreamReader(r.GetEntry("report.md")!.Open()).ReadToEnd());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void CopiesAreRedactedByDefaultAndAnOldDefaultIsNotAChoice()
    {
        Assert.True(new Settings().RedactCopies);
        // Before settings version 4, Save wrote the old default (false) into every file: not a choice, so it is turned on.
        Assert.True(Settings.Parse("""{ "settingsVersion": 3, "redactCopies": false }""").RedactCopies);
        // Turned off after the change: kept.
        Assert.False(Settings.Parse("""{ "settingsVersion": 4, "redactCopies": false }""").RedactCopies);
        // A --config file is read as written.
        Assert.False(Settings.Parse("""{ "redactCopies": false }""", explicitConfig: true).RedactCopies);
    }
}
