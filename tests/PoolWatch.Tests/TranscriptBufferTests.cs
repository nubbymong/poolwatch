using System.Text;
using PoolWatch.Core.Aicc;

namespace PoolWatch.Tests;

/// <summary>
/// The transcript search reads each file's tail through one pooled buffer, a buffer at a time, instead of a fresh array
/// of up to 8 MB per file. It must see exactly the lines the whole-tail read saw: lines across buffer boundaries, a last
/// line without a newline, the tail's first partial line skipped, and lines longer than the line limit skipped; and it
/// must count what it reads against the search's byte budget.
/// </summary>
public sealed class TranscriptBufferTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private const string Command = "git.exe -C /d/repos/abc status --porcelain --untracked-files=no";
    private readonly string _config = Path.Combine(Path.GetTempPath(), "pw-transcripts-" + Guid.NewGuid().ToString("N"));
    private readonly Random _rnd = new(5);

    public void Dispose()
    {
        try { Directory.Delete(_config, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { } // a scanner still holds a file: TEMP is cleaned later
    }

    private static string Hit(string session, int padding = 0) =>
        $"{{\"type\":\"assistant\",\"sessionId\":\"{session}\",\"timestamp\":\"2026-10-20T06:08:00.000Z\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{new string('p', padding)}\"}}," +
        "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{\"command\":\"git -C /d/repos/abc status --porcelain --untracked-files=no\"}}]}}";

    private string Filler(int length) =>
        "{\"type\":\"assistant\",\"sessionId\":\"f\",\"timestamp\":\"2026-10-20T06:01:00.000Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" +
        new string((char)('a' + _rnd.Next(26)), Math.Max(1, length - 110)) + "\"}]}}";

    /// <summary>Writes the only transcript in the config folder (modified within the search window).</summary>
    private void Transcript(IEnumerable<string> lines, bool finalNewline = true)
    {
        string dir = Path.Combine(_config, "projects", "D--repo");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "session.jsonl");
        string text = string.Join("\n", lines) + (finalNewline ? "\n" : "");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, T0.AddMinutes(9));
    }

    private TranscriptHit? Find(SearchBudget budget) => TranscriptSearch.Find([_config], Command, T0.AddMinutes(8).AddSeconds(30), budget: budget);

    /// <summary>A 4 KB line limit: the pooled buffer is 8 KB, so a 40 KB file is read in several pieces.</summary>
    private static readonly SearchBudget Small = new(MaxLineBytes: 4096);

    [Fact]
    public void AToolCallIsFoundWhereverTheBufferBoundariesFall()
    {
        for (int before = 0; before < 40; before++)
        {
            var lines = Enumerable.Range(0, before).Select(_ => Filler(200 + _rnd.Next(3000))).Append(Hit("s" + before)).Concat(Enumerable.Range(0, 5).Select(_ => Filler(900)));
            Transcript(lines);
            Assert.Equal("s" + before, Find(Small)?.SessionId);
        }
    }

    [Fact]
    public void TheLastLineCountsWithoutANewline()
    {
        Transcript([Filler(1000), Filler(3000), Hit("last")], finalNewline: false);
        Assert.Equal("last", Find(Small)?.SessionId);
    }

    [Fact]
    public void LinesOverTheLimitAreSkippedAndLinesAfterThemAreRead()
    {
        Transcript([Filler(900), Hit("too-long", padding: 5000), Filler(900)]); // over the 4 KB limit but inside the buffer
        Assert.Null(Find(Small));
        Transcript([Filler(900), Hit("huge", padding: 30_000), Filler(900)]); // longer than the whole buffer
        Assert.Null(Find(Small));
        Transcript([Filler(40_000), Hit("after"), Filler(900)]);
        Assert.Equal("after", Find(Small)?.SessionId);
    }

    [Fact]
    public void TheTailStartsAtTheFirstWholeLine()
    {
        string hit = Hit("cut");
        // The 2,000-byte tail begins inside the tool call's line: that partial line is never matched.
        Transcript([Filler(5000), hit, Filler(2000 - hit.Length / 2)]);
        Assert.Null(Find(Small with { TailBytes = 2000 }));
        // Beginning exactly at the line's first byte, the tail cannot tell that the line is whole: it is skipped too.
        string after = Filler(900);
        Transcript([Filler(5000), hit, after]);
        Assert.Null(Find(Small with { TailBytes = hit.Length + after.Length + 2 }));
        Transcript([Filler(5000), Filler(300), hit, Filler(900)]);
        Assert.Equal("cut", Find(Small with { TailBytes = 2000 })?.SessionId);
    }

    /// <summary>The search's byte budget (what keeps it from becoming a crawler) counts every piece read.</summary>
    [Fact]
    public void TheSearchStopsOnceItHasReadItsBudget()
    {
        // Three transcripts with 16 KB tails; the tool call is in the one modified furthest from the start, read last.
        string dir = Path.Combine(_config, "projects", "D--repo");
        Directory.CreateDirectory(dir);
        for (int f = 0; f < 3; f++)
        {
            var lines = Enumerable.Range(0, 20).Select(_ => Filler(1000)).ToList();
            if (f == 2) lines.Insert(18, Hit("third"));
            string path = Path.Combine(dir, $"s{f}.jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, T0.AddMinutes(8).AddSeconds(30 + 10 * (f + 1)));
        }
        var tails = Small with { TailBytes = 16 << 10 };
        Assert.Null(Find(tails with { MaxBytes = 2 * (16 << 10) })); // two tails read: nothing left for the third
        Assert.Equal("third", Find(tails with { MaxBytes = 3 * (16 << 10) })?.SessionId);
    }

    [Fact]
    public void ASearchAllocatesNoBufferPerFile()
    {
        // Four transcripts with a 1 MB tail each and no match: every tail byte is read and scanned.
        string dir = Path.Combine(_config, "projects", "D--busy");
        Directory.CreateDirectory(dir);
        for (int f = 0; f < 4; f++)
        {
            string path = Path.Combine(dir, $"s{f}.jsonl");
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 1500).Select(_ => Filler(1000) + "\n")), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, T0.AddMinutes(9));
        }
        var budget = new SearchBudget(TailBytes: 1 << 20, MaxBytes: 64L << 20);
        Assert.Null(Find(budget)); // warm-up: the pool's buffer, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Find(budget));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 << 10, $"a search over 4 MB of tails allocated {allocated / 1024} KB");
    }
}
